using NoDrive.Core.Model;
using NoDrive.Core.Names;
using Xunit;

namespace NoDrive.Core.Tests;

public class NamesAndLocalTests
{
    [Theory]
    [InlineData("report.docx", null)]
    [InlineData("a:b.txt", "invalid-character")]
    [InlineData("what?.txt", "invalid-character")]
    [InlineData("name.", "trailing-dot-or-space")]
    [InlineData("name ", "trailing-dot-or-space")]
    [InlineData("CON", "reserved-name")]
    [InlineData("nul.txt", "reserved-name")]
    [InlineData("", "empty")]
    public void Windows_names_are_checked(string name, string? expected) => Assert.Equal(expected, WindowsNames.Problem(name));

    [Fact]
    public void Too_long_names_are_rejected() => Assert.Equal("too-long", WindowsNames.Problem(new string('a', 256)));

    [Fact]
    public void Local_flag_tracks_items_that_exist_on_disk_and_survives_updates()
    {
        using var t = new TempIndex();
        var idx = t.Index;
        idx.ApplyPage([DeltaChange.Upsert(Items.Folder("F", "root", "Docs")), DeltaChange.Upsert(Items.File("A", "F", "a.txt")), DeltaChange.Upsert(Items.File("B", "F", "b.txt"))], "delta.next", "n", 0);
        Assert.Empty(idx.EnumerateLocalChildren("F"));
        idx.MarkChildrenLocal("F");
        Assert.Equal(2, idx.EnumerateLocalChildren("F").Count());
        idx.ApplyPage([DeltaChange.Upsert(Items.File("A", "F", "renamed.txt", 5))], "delta.next", "n2", 0);   // an update keeps the flag
        Assert.True(idx.TryGet("A")!.Value.Local);
        idx.MarkNotLocal("B");
        Assert.Single(idx.EnumerateLocalChildren("F"));
        Assert.Equal(1, idx.CountLocal() - 0 == 1 ? 1 : 0);
    }

    [Fact]
    public void Release_backoff_makes_deferred_work_due_and_pending_op_kinds_are_separate()
    {
        using var t = new TempIndex();
        var idx = t.Index;
        idx.EnqueueUploads([@"C:\r\x.txt"], 9_000_000_000_000_000);
        idx.EnqueueOp("delete", "A", null, 9_000_000_000_000_000);
        Assert.Empty(idx.DueUploads(100, 10));
        Assert.True(idx.HasPendingOp("A", "delete"));
        Assert.False(idx.HasPendingOp("A", "rename"));
        idx.ReleaseBackoff(100);
        Assert.Single(idx.DueUploads(100, 10));
        Assert.Single(idx.DueOps(100, 10));
        idx.RemoveOps("A", "delete");
        Assert.False(idx.HasPendingOp("A"));
    }

    [Fact]
    public void Quick_check_reports_a_healthy_index()
    {
        using var t = new TempIndex();
        Assert.Null(t.Index.QuickCheck());
    }
}
