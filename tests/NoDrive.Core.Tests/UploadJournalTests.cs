using NoDrive.Core.Model;
using Xunit;

namespace NoDrive.Core.Tests;

public class UploadJournalTests
{
    [Fact]
    public void FindChild_is_case_insensitive()
    {
        using var t = new TempIndex();
        t.Index.ApplyPage([DeltaChange.Upsert(Items.Folder("F1", "root", "Docs"))], "delta.next", "n", 0);
        Assert.Equal("F1", t.Index.FindChild("root", "docs")?.Id);
        Assert.Null(t.Index.FindChild("root", "other"));
    }

    [Fact]
    public void Journal_is_idempotent_ordered_by_depth_and_honours_the_schedule()
    {
        using var t = new TempIndex();
        var idx = t.Index;
        idx.EnqueueUploads([@"C:\r\a\b\deep.txt", @"C:\r\top.txt"], 100);
        idx.EnqueueUploads([@"C:\r\top.txt"], 5000);                          // already queued: schedule unchanged
        Assert.Equal(2, idx.CountUploads());
        var due = idx.DueUploads(100, 10);
        Assert.Equal([@"C:\r\top.txt", @"C:\r\a\b\deep.txt"], due.Select(d => d.Path));
        idx.RescheduleUpload(@"C:\r\top.txt", 1, 9000);
        Assert.Single(idx.DueUploads(100, 10));
        idx.RemoveUpload(@"C:\r\a\b\deep.txt");
        Assert.Empty(idx.DueUploads(100, 10));
        Assert.Equal(1, idx.CountUploads());
    }
}
