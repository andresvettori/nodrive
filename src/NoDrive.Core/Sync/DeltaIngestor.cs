using NoDrive.Core.Abstractions;
using NoDrive.Core.Index;
using NoDrive.Core.Model;

namespace NoDrive.Core.Sync;

/// <summary>Result of one ingestion run.</summary>
public sealed record IngestResult(int Pages, long Upserts, long Deletions, bool FullResync, string DeltaLink);

/// <summary>
/// Streams delta pages into the index (design: one transaction per page, resume token in the same transaction, deletions first).
/// - Resumes an interrupted enumeration from the stored nextLink.
/// - Runs an incremental round from the stored deltaLink.
/// - On <see cref="ResyncRequiredException"/> (410 Gone) restarts a full enumeration under a new generation and prunes rows not seen again.
/// </summary>
public sealed class DeltaIngestor(IRemoteDrive drive, ItemIndex index)
{
    public const string NextKey = "delta.next";
    public const string DeltaKey = "delta.link";

    /// <summary>Progress callback, invoked after each page: (pages so far, rows in the index is not queried here to stay cheap).</summary>
    public Action<int, long>? OnProgress { get; init; }

    /// <summary>
    /// Optional hook around each page: called before the page is applied to the index (old state still readable), returns an action run
    /// right after (new state readable). Args: changes, whether this is a full re-enumeration. Must not throw; exceptions are swallowed so the index always advances.
    /// </summary>
    public Func<IReadOnlyList<DeltaChange>, bool, Action?>? BeforePage { get; init; }

    public async Task<IngestResult> RunAsync(CancellationToken cancellationToken)
    {
        string? resume = index.GetMeta(NextKey) ?? index.GetMeta(DeltaKey);
        try
        {
            return await RunFromAsync(resume, fullResync: false, index.Generation, cancellationToken).ConfigureAwait(false);
        }
        catch (ResyncRequiredException)
        {
            // The token is dead: forget it, bump the generation, re-enumerate everything, then prune what no longer exists.
            int generation = index.Generation + 1;
            index.SetMeta(NextKey, null);
            index.SetMeta(DeltaKey, null);
            index.SetMeta("generation", generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var result = await RunFromAsync(null, fullResync: true, generation, cancellationToken).ConfigureAwait(false);
            index.PruneOlderThan(generation);
            return result;
        }
    }

    private async Task<IngestResult> RunFromAsync(string? resume, bool fullResync, int generation, CancellationToken ct)
    {
        int pages = 0;
        long ups = 0, dels = 0;
        string? deltaLink = null;
        await foreach (var page in drive.GetDeltaAsync(resume, ct).ConfigureAwait(false))
        {
            bool last = page.NextLink is null;
            string key = last ? DeltaKey : NextKey;
            string token = page.NextLink ?? page.DeltaLink ?? throw new InvalidOperationException("A delta page must carry a nextLink or a deltaLink.");
            var changes = ItemIndex.LastPerId(page.Changes);
            Action? after = null;
            try { after = BeforePage?.Invoke(changes, fullResync); } catch (Exception) { /* local mirroring is best effort */ }
            var (u, d) = index.ApplyPage(changes, key, token, generation);
            try { after?.Invoke(); } catch (Exception) { }
            ups += u; dels += d; pages++;
            if (last)
            {
                // The enumeration is complete: the in-flight resume token is obsolete, the delta link becomes the new baseline.
                index.SetMeta(NextKey, null);
                deltaLink = token;
            }
            OnProgress?.Invoke(pages, ups);
        }
        return new IngestResult(pages, ups, dels, fullResync, deltaLink ?? index.GetMeta(DeltaKey) ?? "");
    }
}
