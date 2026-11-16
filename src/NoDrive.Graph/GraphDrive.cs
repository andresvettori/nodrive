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

    /// <summary>Optional upload rate limit shared by all uploads.</summary>
    public NoDrive.Core.Net.BandwidthLimiter? UploadLimiter { get; set; }

    /// <summary>Reports the bytes that went out, for the activity counters.</summary>
    public Action<long>? OnBytesUploaded { get; set; }
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

    /// <summary>Uploads up to this size with one PUT; larger files use a resumable upload session in 5 MiB chunks (a multiple of 320 KiB).</summary>
    public const long SimpleUploadLimit = 4 * 1024 * 1024;
    private const int ChunkSize = 5 * 1024 * 1024;

    private static RemoteItem ToRemote(DriveItemDto dto) => new(dto.Id, dto.Parent?.Id, dto.Name ?? "", dto.Size, dto.LastModified ?? DateTimeOffset.UnixEpoch, dto.Folder is not null, dto.ETag, dto.CTag, dto.File?.Hashes?.QuickXorHash);

    private string ChildUrl(string parentId, string name, string suffix) => $"{_options.BaseUrl}/me/drive/items/{Uri.EscapeDataString(parentId)}:/{Uri.EscapeDataString(name)}:{suffix}";

    /// <summary>
    /// Uploads a NEW file. conflictBehavior=fail: an existing name is never replaced; the caller gets an HttpRequestException with
    /// StatusCode Conflict and picks another name. Throws if the file changes size while reading. Never overwrites, never deletes.
    /// </summary>
    public async Task<RemoteItem> UploadNewFileAsync(string parentId, string name, string localPath, CancellationToken ct, Action<long, long>? progress = null)
    {
        long size = new FileInfo(localPath).Length;
        if (size <= SimpleUploadLimit)
        {
            if (UploadLimiter is not null) { await UploadLimiter.WaitAsync(size, ct).ConfigureAwait(false); }
            using var resp = await SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Put, ChildUrl(parentId, name, "/content?@microsoft.graph.conflictBehavior=fail"));
                req.Content = new StreamContent(new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan));
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return req;
            }, ct).ConfigureAwait(false);
            OnBytesUploaded?.Invoke(size);
            progress?.Invoke(size, size);
            return await ReadItemAsync(resp, ct).ConfigureAwait(false);
        }

        string uploadUrl;
        using (var resp = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, ChildUrl(parentId, name, "/createUploadSession"));
            req.Content = new StringContent("{\"item\":{\"@microsoft.graph.conflictBehavior\":\"fail\"}}", System.Text.Encoding.UTF8, "application/json");
            return req;
        }, ct).ConfigureAwait(false))
        {
            var s = await resp.Content.ReadFromJsonAsync(GraphJsonContext.Default.UploadSessionDto, ct).ConfigureAwait(false);
            uploadUrl = s?.UploadUrl ?? throw new InvalidOperationException("No upload URL returned.");
        }

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            await using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            if (fs.Length != size) { throw new IOException("The file changed while uploading."); }
            long sent = 0;
            while (sent < size)
            {
                int n = await fs.ReadAtLeastAsync(buffer.AsMemory(0, (int)Math.Min(ChunkSize, size - sent)), (int)Math.Min(ChunkSize, size - sent), throwOnEndOfStream: true, ct).ConfigureAwait(false);
                if (UploadLimiter is not null) { await UploadLimiter.WaitAsync(n, ct).ConfigureAwait(false); }
                for (int attempt = 1; ; attempt++)
                {
                    // Fragment PUTs carry no Authorization header (the URL is pre-authenticated).
                    using var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ReadOnlyMemoryContent(buffer.AsMemory(0, n)) };
                    req.Content.Headers.ContentRange = new ContentRangeHeaderValue(sent, sent + n - 1, size);
                    req.Content.Headers.ContentLength = n;
                    try
                    {
                        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        if (resp.StatusCode == HttpStatusCode.Conflict) { throw new HttpRequestException("Graph 409 Conflict (upload session)", null, HttpStatusCode.Conflict); }
                        if ((int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            if (attempt >= _options.MaxAttempts) { throw new HttpRequestException($"Graph {(int)resp.StatusCode} during upload", null, resp.StatusCode); }
                            await Task.Delay(resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(30, 3 * attempt)), ct).ConfigureAwait(false);
                            continue;
                        }
                        resp.EnsureSuccessStatusCode();
                        sent += n;
                        OnBytesUploaded?.Invoke(n);
                        progress?.Invoke(sent, size);
                        if (sent >= size) { return await ReadItemAsync(resp, ct).ConfigureAwait(false); }
                        break;
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < _options.MaxAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 3 * attempt)), ct).ConfigureAwait(false);       // dropped connection: resend the same chunk
                    }
                }
            }
            throw new InvalidOperationException("Upload ended without a final response.");
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>Creates a folder; an existing name fails with Conflict (never merged or renamed by the server).</summary>
    public async Task<RemoteItem> CreateFolderAsync(string parentId, string name, CancellationToken ct)
    {
        using var resp = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/me/drive/items/{Uri.EscapeDataString(parentId)}/children");
            req.Content = new StringContent($"{{\"name\":\"{System.Text.Json.JsonEncodedText.Encode(name)}\",\"folder\":{{}},\"@microsoft.graph.conflictBehavior\":\"fail\"}}", System.Text.Encoding.UTF8, "application/json");
            return req;
        }, ct).ConfigureAwait(false);
        return await ReadItemAsync(resp, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes an item into the OneDrive recycle bin. With an eTag the delete is refused (412) if the item changed meanwhile; a missing item counts as deleted.</summary>
    public async Task DeleteItemAsync(string itemId, string? etag, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Delete, $"{_options.BaseUrl}/me/drive/items/{Uri.EscapeDataString(itemId)}");
                if (!string.IsNullOrEmpty(etag)) { req.Headers.TryAddWithoutValidation("If-Match", etag); }
                return req;
            }, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
    }

    /// <summary>Renames and/or moves an item. conflictBehavior=fail: a name clash returns 409 and nothing is replaced.</summary>
    public async Task<RemoteItem> MoveItemAsync(string itemId, string newName, string? newParentId, string? etag, CancellationToken ct)
    {
        string parent = newParentId is null ? "" : $",\"parentReference\":{{\"id\":\"{System.Text.Json.JsonEncodedText.Encode(newParentId)}\"}}";
        string body = $"{{\"name\":\"{System.Text.Json.JsonEncodedText.Encode(newName)}\"{parent},\"@microsoft.graph.conflictBehavior\":\"fail\"}}";
        using var resp = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Patch, $"{_options.BaseUrl}/me/drive/items/{Uri.EscapeDataString(itemId)}") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            if (!string.IsNullOrEmpty(etag)) { req.Headers.TryAddWithoutValidation("If-Match", etag); }
            return req;
        }, ct).ConfigureAwait(false);
        return await ReadItemAsync(resp, ct).ConfigureAwait(false);
    }

    private static async Task<RemoteItem> ReadItemAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var dto = await resp.Content.ReadFromJsonAsync(GraphJsonContext.Default.DriveItemDto, ct).ConfigureAwait(false);
        return dto is null ? throw new InvalidOperationException("Empty item response.") : ToRemote(dto);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken ct) => SendAsync(() => new HttpRequestMessage(method, url), ct);

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            using var req = makeRequest();
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
