using NoDrive.Core.Model;
using Xunit;

namespace NoDrive.Core.Tests;

/// <summary>
/// Things the Graph delta feed is known to do that a naive ingestor gets wrong: the same id more than once (the LAST occurrence wins), a delete after an
/// upsert, deletes of unknown ids, children before their parent, items without a parent or hash, and renamed folders whose children are not repeated.
/// </summary>
public class DeltaQuirksTests
{
    private static DeltaChange Up(RemoteItem i) => DeltaChange.Upsert(i);

    private static void Apply(TempIndex t, params DeltaChange[] changes) => t.Index.ApplyPage(changes, "delta.next", "n", t.Index.Generation);

    [Fact]
    public void The_same_id_twice_in_a_page_keeps_the_last_occurrence()
    {
        using var t = new TempIndex();
        Apply(t, Up(Items.File("a", "root", "old.txt", 1)), Up(Items.File("a", "root", "new.txt", 2)));
        var a = t.Index.TryGet("a");
        Assert.Equal("new.txt", a!.Value.Name);
        Assert.Equal(2, a.Value.Size);
    }

    [Fact]
    public void An_upsert_followed_by_a_delete_of_the_same_id_leaves_it_deleted()
    {
        using var t = new TempIndex();
        Apply(t, Up(Items.File("a", "root", "a.txt")), DeltaChange.Delete("a"));
        Assert.Null(t.Index.TryGet("a"));
    }

    [Fact]
    public void A_delete_followed_by_an_upsert_of_the_same_id_is_a_restore()
    {
        using var t = new TempIndex();
        Apply(t, Up(Items.File("a", "root", "a.txt")));
        Apply(t, DeltaChange.Delete("a"), Up(Items.File("a", "root", "a.txt")));
        Assert.NotNull(t.Index.TryGet("a"));
    }

    [Fact]
    public void Deleting_an_unknown_id_is_harmless_and_not_counted()
    {
        using var t = new TempIndex();
        var (ups, dels) = t.Index.ApplyPage([DeltaChange.Delete("never-seen")], "delta.next", "n", t.Index.Generation);
        Assert.Equal(0, ups);
        Assert.Equal(0, dels);
    }

    [Fact]
    public void A_child_arriving_before_its_parent_is_stored_and_found_once_the_parent_arrives()
    {
        using var t = new TempIndex();
        Apply(t, Up(Items.File("c", "F", "child.txt")));
        Apply(t, Up(Items.Folder("F", "root", "Docs")));
        Assert.Equal("c", t.Index.FindChild("F", "child.txt")!.Value.Id);
        Assert.Equal("F", t.Index.FindChild("root", "Docs")!.Value.Id);
    }

    [Fact]
    public void An_item_without_a_parent_or_hash_is_stored_without_failing()
    {
        using var t = new TempIndex();
        var noParent = new RemoteItem("x", null, "orphan.txt", 0, Items.T0, false, "e", "c", null);
        Apply(t, Up(noParent));
        var x = t.Index.TryGet("x");
        Assert.NotNull(x);
        Assert.Null(x.Value.QuickXorHash);
    }

    [Fact]
    public void Renaming_a_folder_does_not_touch_its_children_which_the_feed_does_not_repeat()
    {
        using var t = new TempIndex();
        Apply(t, Up(Items.Folder("F", "root", "Docs")), Up(Items.File("a", "F", "a.txt")));
        Apply(t, Up(Items.Folder("F", "root", "Documents")));                                    // only the folder is in the feed
        Assert.Equal("a", t.Index.FindChild("F", "a.txt")!.Value.Id);
        Assert.Null(t.Index.FindChild("root", "Docs"));
        Assert.Equal("F", t.Index.FindChild("root", "Documents")!.Value.Id);
    }

    [Fact]
    public void Moving_a_file_to_another_folder_is_a_parent_change_of_the_same_id()
    {
        using var t = new TempIndex();
        Apply(t, Up(Items.Folder("F", "root", "A")), Up(Items.Folder("G", "root", "B")), Up(Items.File("a", "F", "a.txt")));
        Apply(t, Up(Items.File("a", "G", "a.txt")));
        Assert.Null(t.Index.FindChild("F", "a.txt"));
        Assert.Equal("a", t.Index.FindChild("G", "a.txt")!.Value.Id);
    }
}
