using System.Diagnostics;
using NoDrive.Core.Index;
using NoDrive.Core.Model;
using NoDrive.Core.Population;
using NoDrive.Engine.Infrastructure;

namespace NoDrive.Engine.CloudFiles;

/// <summary>
/// Populates heavy folders BEFORE the user opens them (plan E3c, ADR-0007 amendment), so Explorer never freezes while Windows creates tens
/// of thousands of placeholders (about 160 us each, not tunable). Works from <see cref="PopulationPlanner"/> (cost-based, ancestors first,
/// within a budget) on a below-normal thread with short pauses so foreground I/O is never starved (measured: probe p95 stayed at 7 ms).
/// </summary>
internal sealed class Prefetcher(ItemIndex index, PopulationState state, FolderLocks locks, string syncRoot, PopulationOptions options)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var plan = PopulationPlanner.Plan(index, state.RootId, options, state.PopulatedSet());
        if (plan.Count == 0) { Log.Info("prefetch: nothing to do (no heavy folders left to populate)"); return; }
        long total = plan.Sum(s => s.Children);
        Log.Info($"prefetch: {plan.Count} folders, {total:N0} placeholders (heavy threshold {options.HeavyThreshold} children, budget {options.PlaceholderBudget:N0})");
        var sw = Stopwatch.StartNew();
        int done = 0;
        foreach (var step in plan)
        {
            ct.ThrowIfCancellationRequested();
            var gate = locks.For(step.FolderId);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (state.IsPopulated(step.FolderId)) { continue; }                      // the user opened it first: the on-demand path populated it
                string? dir = DirectoryOf(step.FolderId);
                if (dir is null || !Directory.Exists(dir)) { Log.Warn($"prefetch: directory for {step.FolderId} is not on disk yet; skipped"); continue; }
                var stepSw = Stopwatch.StartNew();
                var batch = new List<IndexedItem>(PlaceholderWriter.DefaultBatch);
                int created = 0;
                foreach (var child in ChildFilter.Valid(index.EnumerateChildren(step.FolderId)))
                {
                    batch.Add(child);
                    if (batch.Count < PlaceholderWriter.DefaultBatch) { continue; }
                    created += PlaceholderWriter.CreateChildren(dir, batch);
                    batch.Clear();
                    await Task.Delay(20, ct).ConfigureAwait(false);                       // yield: foreground listings must not wait on us
                }
                if (batch.Count > 0) { created += PlaceholderWriter.CreateChildren(dir, batch); }
                if (PlaceholderWriter.MarkPopulated(dir)) { state.Set(step.FolderId, true); }
                done++;
                if (stepSw.ElapsedMilliseconds > 500) { Log.Info($"prefetch: {Path.GetFileName(dir)}: {created:N0} placeholders in {stepSw.ElapsedMilliseconds} ms"); }
            }
            finally { gate.Release(); }
        }
        Log.Info($"prefetch: done, {done} folders in {sw.Elapsed.TotalSeconds:F1} s");
    }

    private string? DirectoryOf(string folderId)
    {
        if (folderId == state.RootId) { return syncRoot; }
        var names = new List<string>();
        foreach (var id in index.PathTo(folderId, state.RootId))
        {
            string? n = index.GetName(id);
            if (n is null) { return null; }
            names.Add(n);
        }
        return names.Count == 0 ? null : Path.Combine([syncRoot, .. names]);
    }
}
