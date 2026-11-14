using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NoDrive.Core.Abstractions;
using NoDrive.Core.Model;
using NoDrive.Graph.Json;

namespace NoDrive.Graph;

/// <summary>Source of fresh access tokens (the authenticator in production, a constant in tests).</summary>
public delegate ValueTask<string> AccessTokenProvider(CancellationToken cancellationToken);

/// <summary>Tunables of the Graph client.</summary>
public sealed record GraphOptions
{
    public string BaseUrl { get; init; } = GraphEndpoints.BaseUrl;
    public int PageSize { get; init; } = 1000;

    /// <summary>Background enumeration may wait out long throttling pauses (nothing user-facing depends on it); capped here.</summary>
    public TimeSpan MaxBackgroundRetryAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Attempts per page for transient failures (5xx, throttling).</summary>
    public int MaxAttempts { get; init; } = 5;
}

/// <summary>
/// OneDrive through Microsoft Graph v1.0 (personal accounts). HttpClient + System.Text.Json source generation, no SDK (ADR-0004).
/// Delta semantics follow research/graph-onedrive.md: track by id, deletions carry the `deleted` facet, 410 means resync, and
/// the root item is not part of the tree we store.
/// </summary>
public sealed class GraphDrive(HttpClient http, AccessTokenProvider accessToken, ThrottleGate? gate = null, GraphOptions? options = null) : IRemoteDrive
{
    private const string SelectFields = "id,name,size,lastModifiedDateTime,folder,file,root,parentReference,deleted,cTag,eTag";
    private readonly GraphOptions _options = options ?? new GraphOptions();
    private readonly ThrottleGate _gate = gate ?? new ThrottleGate();

    /// <summary>The id of the drive root (parent of the top-level items in delta), needed by the engine to anchor the tree.</summary>
    public async Task<string> GetRootIdAsync(CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"{_options.BaseUrl}/me/drive/root?$select=id", ct).ConfigureAwait(false);
        var dto = await resp.Content.ReadFromJsonAsync(GraphJsonContext.Default.DriveItemDto, ct).ConfigureAwait(false);
        return dto?.Id ?? throw new InvalidOperationException("Graph returned no root id.");
    }

    public async IAsyncEnumerable<DeltaPage> GetDeltaAsync(string? resumeLink, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? url = resumeLink ?? $"{_options.BaseUrl}/me/drive/root/delta?$select={SelectFields}&$top={_options.PageSize}";
        while (url is not null)
        {
            DeltaResponse body;
            using (var resp = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false))
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                body = await JsonSerializer.DeserializeAsync(stream, GraphJsonContext.Default.DeltaResponse, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Empty delta response.");
            }
            var changes = new List<DeltaChange>(body.Value.Count);
            foreach (var dto in body.Value)
            {
                if (dto.Root is not null) { continue; }                               // the drive root is the anchor, not a stored item
                if (dto.Deleted is not null) { changes.Add(DeltaChange.Delete(dto.Id)); continue; }
                changes.Add(DeltaChange.Upsert(new RemoteItem(
                    dto.Id,
                    dto.Parent?.Id,
                    dto.Name ?? "",
                    dto.Size,
                    dto.LastModified ?? DateTimeOffset.UnixEpoch,
                    dto.Folder is not null,
                    dto.ETag,
                    dto.CTag,
                    dto.File?.Hashes?.QuickXorHash)));
            }
            yield return new DeltaPage(changes, body.NextLink, body.DeltaLink);
            url = body.NextLink;
        }
    }

    /// <summary>
    /// A fresh pre-authenticated download URL for an item. Measured 2026-11-14: the property is present on a plain item GET and
    /// absent when `$select` is used, so no `$select` here. URLs live about an hour; do not cache them longer than 45 minutes.
    /// </summary>
    public async Task<Uri> GetDownloadUrlAsync(string itemId, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"{_options.BaseUrl}/me/drive/items/{Uri.EscapeDataString(itemId)}", ct).ConfigureAwait(false);
        var dto = await resp.Content.ReadFromJsonAsync(GraphJsonContext.Default.DriveItemDto, ct).ConfigureAwait(false);
        return dto?.DownloadUrl is { Length: > 0 } u ? new Uri(u) : throw new InvalidOperationException($"Item {itemId} has no download URL (folder or unavailable).");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct).ConfigureAwait(false));
            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            try
            {
                if (resp.IsSuccessStatusCode) { return resp; }

                if (resp.StatusCode == HttpStatusCode.Gone) { throw new ResyncRequiredException(await ReadErrorAsync(resp, ct).ConfigureAwait(false)); }

                if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < _options.MaxAttempts)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, 5 * attempt));
                    if (wait > _options.MaxBackgroundRetryAfter) { wait = _options.MaxBackgroundRetryAfter; }
                    _gate.PauseFor(wait);                                              // every other request waits too
                    continue;
                }

                if ((int)resp.StatusCode >= 500 && attempt < _options.MaxAttempts)
                {
                    _gate.PauseFor(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)));
                    continue;
                }

                throw new HttpRequestException($"Graph {(int)resp.StatusCode} {resp.StatusCode}: {await ReadErrorAsync(resp, ct).ConfigureAwait(false)}", null, resp.StatusCode);
            }
            catch
            {
                resp.Dispose();
                throw;
            }
            finally
            {
                if (!resp.IsSuccessStatusCode) { resp.Dispose(); }
            }
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var err = await resp.Content.ReadFromJsonAsync(GraphJsonContext.Default.GraphError, ct).ConfigureAwait(false);
            return $"{err?.Error?.Code}: {err?.Error?.Message}";
        }
        catch (JsonException) { return resp.ReasonPhrase ?? "error"; }
    }
}
