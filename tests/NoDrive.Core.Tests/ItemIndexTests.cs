using NoDrive.Core.Index;
using NoDrive.Core.Model;
using Xunit;

namespace NoDrive.Core.Tests;

public class ItemIndexTests
{
    private static DeltaChange Up(RemoteItem i) => DeltaChange.Upsert(i);

    [Fact]
    public void ApplyPage_inserts_updates_and_deletes_in_one_transaction_with_the_resume_token()
    {
        using var t = new TempIndex();
        var idx = t.Index;
        idx.ApplyPage([Up(Items.Folder("F1", "root", "Docs")), Up(Items.File("A", "F1", "a.txt")), Up(Items.File("B", "F1", "b.txt"))], "delta.next", "next:1", 0);
        Assert.Equal(3, idx.Count());
        Assert.Equal("next:1", idx.GetMeta("delta.next"));

        var (ups, dels) = idx.ApplyPage([Up(Items.File("A", "F1", "renamed.txt", 99)), DeltaChange.Delete("B"), DeltaChange.Delete("missing")], "delta.next", "next:2", 0);
        Assert.Equal(1, ups);
        Assert.Equal(1, dels);                       // only the delete that hit a row is counted
        Assert.Equal(2, idx.Count());
        var a = idx.TryGet("A");
        Assert.NotNull(a);
        Assert.Equal("renamed.txt", a.Value.Name);
        Assert.Equal(99, a.Value.Size);
        Assert.Null(idx.TryGet("B"));
    }

    [Fact]
    public void Deletions_are_applied_before_upserts_so_a_recreated_id_survives()
    {
        using var t = new TempIndex();
        t.Index.ApplyPage([Up(Items.File("X", "root", "old.txt"))], "k", "1", 0);
        t.Index.ApplyPage([Up(Items.File("X", "root", "new.txt")), DeltaChange.Delete("X")], "k", "2", 0);
        Assert.Equal("new.txt", t.Index.TryGet("X")!.Value.Name);
    }

    [Fact]
    public void EnumerateChildren_streams_in_name_order_and_counts_match()
    {
        using var t = new TempIndex();
        var changes = new List<DeltaChange> { Up(Items.Folder("D", "root", "Dir")) };
        foreach (var n in new[] { "zeta", "alpha", "mid", "Beta" }) { changes.Add(Up(Items.File("f-" + n, "D", n))); }
        t.Index.ApplyPage(changes, "k", "1", 0);
        Assert.Equal(4, t.Index.CountChildren("D"));
        var names = t.Index.EnumerateChildren("D").Select(i => i.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);   // SQLite BINARY collation: ordinal
        Assert.Equal(4, names.Count);
    }

    [Fact]
    public void HeavyFolders_returns_only_folders_at_or_above_the_threshold_largest_first()
    {
        using var t = new TempIndex();
        var changes = new List<DeltaChange> { Up(Items.Folder("small", "root", "s")), Up(Items.Folder("big", "root", "b")), Up(Items.Folder("huge", "root", "h")) };
        for (int i = 0; i < 3; i++) { changes.Add(Up(Items.File("s" + i, "small", "f" + i))); }
        for (int i = 0; i < 10; i++) { changes.Add(Up(Items.File("b" + i, "big", "f" + i))); }
        for (int i = 0; i < 25; i++) { changes.Add(Up(Items.File("h" + i, "huge", "f" + i))); }
        t.Index.ApplyPage(changes, "k", "1", 0);
        var heavy = t.Index.HeavyFolders(10);
        Assert.Equal([("huge", 25L), ("big", 10L)], heavy);
    }

    [Fact]
    public void PathTo_returns_the_folder_chain_below_the_root()
    {
        using var t = new TempIndex();
        t.Index.ApplyPage([Up(Items.Folder("a", "root", "A")), Up(Items.Folder("b", "a", "B")), Up(Items.Folder("c", "b", "C"))], "k", "1", 0);
        Assert.Equal(["a", "b", "c"], t.Index.PathTo("c", "root"));
        Assert.Equal(["a"], t.Index.PathTo("a", "root"));
        Assert.Empty(t.Index.PathTo("root", "root"));
    }

    [Fact]
    public void PruneOlderThan_removes_rows_not_seen_in_the_new_generation()
    {
        using var t = new TempIndex();
        t.Index.ApplyPage([Up(Items.File("keep", "root", "k")), Up(Items.File("gone", "root", "g"))], "k", "1", 0);
        t.Index.ApplyPage([Up(Items.File("keep", "root", "k"))], "k", "2", 1);
        Assert.Equal(1, t.Index.PruneOlderThan(1));
        Assert.NotNull(t.Index.TryGet("keep"));
        Assert.Null(t.Index.TryGet("gone"));
    }

    [Fact]
    public void State_and_population_flags_survive_reopening_the_database()
    {
        var t = new TempIndex();
        try
        {
            t.Index.ApplyPage([Up(Items.Folder("F", "root", "F"))], "delta.link", "delta:1", 0);
            t.Index.SetPopulated("F", true);
            string path = t.DbPath;
            t.Index.Dispose();
            using var again = new ItemIndex(path);
            Assert.True(again.TryGet("F")!.Value.Populated);
            Assert.Equal("delta:1", again.GetMeta("delta.link"));
            again.ResetPopulation();
            Assert.False(again.TryGet("F")!.Value.Populated);
        }
        finally { t.Dispose(); }
    }
}
