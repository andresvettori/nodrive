using NoDrive.Core.Index;

namespace NoDrive.Core.Population;

/// <summary>Tunables of the background population policy (projectPlan E3c, ADR-0007 amendment).</summary>
public sealed record PopulationOptions
{
    /// <summary>Wait a user may notice when a folder is opened cold (measured: perceptible from about 300 ms).</summary>
    public TimeSpan AcceptableWait { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Windows' cost to create one placeholder; re-measure at run time and pass the measured value.</summary>
    public TimeSpan CostPerPlaceholder { get; init; } = TimeSpan.FromMicroseconds(160);

    /// <summary>Upper bound on placeholders created ahead of use (about 0.6 KB of disk each: 150,000 is about 90 MB).</summary>
    public long PlaceholderBudget { get; init; } = 150_000;

    /// <summary>A folder is "heavy" when populating it on demand would exceed <see cref="AcceptableWait"/>.</summary>
    public long HeavyThreshold => Math.Max(1, (long)(AcceptableWait.Ticks / Math.Max(1, CostPerPlaceholder.Ticks)));
}

/// <summary>One folder to populate: its id and how many placeholders populating it creates.</summary>
public sealed record PopulationStep(string FolderId, long Children, bool IsAncestor);

/// <summary>
/// Decides which folders to populate before the user opens them. Policy: by cost, not by depth. Only heavy folders qualify; each is
/// preceded by its ancestor chain (a folder's directory exists only once its parent is populated); ancestors are usually light, so the
/// chain is cheap. Order: largest first. Folders already populated are skipped. The total of created placeholders stays within the budget.
/// </summary>
public static class PopulationPlanner
{
    public static IReadOnlyList<PopulationStep> Plan(ItemIndex index, string rootId, PopulationOptions options, IReadOnlySet<string>? alreadyPopulated = null)
    {
        alreadyPopulated ??= new HashSet<string>();
        var done = new HashSet<string>(alreadyPopulated);
        var steps = new List<PopulationStep>();
        long spent = 0;

        // The root is always populated first when there is any work (its placeholders are the entry point of every chain).
        foreach (var (folderId, children) in index.HeavyFolders(options.HeavyThreshold))
        {
            var chain = index.PathTo(folderId, rootId);              // first folder under the root ... folderId
            var needed = new List<PopulationStep>();
            long cost = 0;
            if (!done.Contains(rootId)) { long rc = index.CountChildren(rootId); needed.Add(new PopulationStep(rootId, rc, true)); cost += rc; }
            foreach (var ancestor in chain)
            {
                if (ancestor == folderId || done.Contains(ancestor)) { continue; }
                long ac = index.CountChildren(ancestor);
                needed.Add(new PopulationStep(ancestor, ac, true));
                cost += ac;
            }
            if (!done.Contains(folderId)) { needed.Add(new PopulationStep(folderId, children, false)); cost += children; }

            if (needed.Count == 0) { continue; }
            if (spent + cost > options.PlaceholderBudget) { continue; }   // too expensive for the remaining budget; smaller heavy folders may still fit
            foreach (var s in needed) { if (done.Add(s.FolderId)) { steps.Add(s); } }
            spent += cost;
        }
        return steps;
    }
}
