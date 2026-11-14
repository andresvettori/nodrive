using NoDrive.Core.Model;
using NoDrive.Core.Population;
using Xunit;

namespace NoDrive.Core.Tests;

public class PopulationPlannerTests
{
    private static DeltaChange Up(RemoteItem i) => DeltaChange.Upsert(i);

    private static void AddFiles(List<DeltaChange> changes, string parent, int count, string prefix)
    {
        for (int i = 0; i < count; i++) { changes.Add(Up(Items.File($"{prefix}{i}", parent, $"{prefix}{i}.dat"))); }
    }

    [Fact]
    public void Heavy_threshold_follows_the_measured_cost_model()
    {
        Assert.Equal(1875, new PopulationOptions().HeavyThreshold);                                                          // 300 ms / 160 us
        Assert.Equal(300, new PopulationOptions { CostPerPlaceholder = TimeSpan.FromMilliseconds(1) }.HeavyThreshold);      // a slower machine prefetches earlier
    }

    [Fact]
    public void Only_heavy_folders_are_planned_each_after_its_ancestor_chain_and_light_siblings_are_ignored()
    {
        using var t = new TempIndex();
        var c = new List<DeltaChange>
        {
            Up(Items.Folder("photos", "root", "Photos")), Up(Items.Folder("2024", "photos", "2024")), Up(Items.Folder("camera", "2024", "Camera")),
            Up(Items.Folder("docs", "root", "Docs")), Up(Items.Folder("small", "photos", "Small")),
        };
        AddFiles(c, "camera", 4500, "c");     // heavy
        AddFiles(c, "docs", 40, "d");         // light
        AddFiles(c, "small", 25, "s");        // light
        t.Index.ApplyPage(c, "k", "1", 0);

        var plan = PopulationPlanner.Plan(t.Index, "root", new PopulationOptions());
        Assert.Equal(["root", "photos", "2024", "camera"], plan.Select(s => s.FolderId));
        Assert.True(plan[0].IsAncestor && plan[1].IsAncestor && plan[2].IsAncestor);
        Assert.False(plan[3].IsAncestor);
        Assert.DoesNotContain(plan, s => s.FolderId is "docs" or "small");
    }

    [Fact]
    public void Folders_already_populated_are_skipped_and_the_shared_chain_is_planned_once()
    {
        using var t = new TempIndex();
        var c = new List<DeltaChange> { Up(Items.Folder("p", "root", "P")), Up(Items.Folder("h1", "p", "H1")), Up(Items.Folder("h2", "p", "H2")) };
        AddFiles(c, "h1", 3000, "a");
        AddFiles(c, "h2", 2000, "b");
        t.Index.ApplyPage(c, "k", "1", 0);

        var plan = PopulationPlanner.Plan(t.Index, "root", new PopulationOptions());
        Assert.Equal(["root", "p", "h1", "h2"], plan.Select(s => s.FolderId));          // largest heavy folder first; "root" and "p" once

        var skip = PopulationPlanner.Plan(t.Index, "root", new PopulationOptions(), new HashSet<string> { "root", "p", "h1" });
        Assert.Equal(["h2"], skip.Select(s => s.FolderId));
    }

    [Fact]
    public void The_budget_caps_the_placeholders_created_ahead_of_use_but_smaller_heavy_folders_may_still_fit()
    {
        using var t = new TempIndex();
        var c = new List<DeltaChange> { Up(Items.Folder("big", "root", "Big")), Up(Items.Folder("mid", "root", "Mid")) };
        AddFiles(c, "big", 6000, "a");
        AddFiles(c, "mid", 2500, "b");
        t.Index.ApplyPage(c, "k", "1", 0);

        var plan = PopulationPlanner.Plan(t.Index, "root", new PopulationOptions { PlaceholderBudget = 3000 });
        Assert.Equal(["root", "mid"], plan.Select(s => s.FolderId));                      // "big" (6000) exceeds the budget, "mid" fits after root
    }
}
