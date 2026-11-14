using NoDrive.Core.Abstractions;
using NoDrive.Core.Model;
using NoDrive.Core.Sync;
using Xunit;

namespace NoDrive.Core.Tests;

public class DeltaIngestorTests
{
    private static DeltaChange Up(RemoteItem i) => DeltaChange.Upsert(i);

    private static ScriptedDrive ThreePageDrive()
    {
        var d = new ScriptedDrive();
        d.FullPages.Add(ScriptedDrive.Page(0, 3, Up(Items.Folder("F", "root", "Docs")), Up(Items.File("a", "F", "a.txt"))));
        d.FullPages.Add(ScriptedDrive.Page(1, 3, Up(Items.File("b", "F", "b.txt")), Up(Items.File("c", "F", "c.txt"))));
        d.FullPages.Add(ScriptedDrive.Page(2, 3, Up(Items.File("d", "F", "d.txt"))));
        return d;
    }

    [Fact]
    public async Task Full_enumeration_stores_every_item_and_ends_with_a_delta_link_and_no_next_link()
    {
        using var t = new TempIndex();
        var result = await new DeltaIngestor(ThreePageDrive(), t.Index).RunAsync(CancellationToken.None);
        Assert.Equal(3, result.Pages);
        Assert.Equal(5, t.Index.Count());
        Assert.Equal("delta:1", t.Index.GetMeta(DeltaIngestor.DeltaKey));
        Assert.Null(t.Index.GetMeta(DeltaIngestor.NextKey));
        Assert.False(result.FullResync);
    }

    [Fact]
    public async Task An_interrupted_enumeration_resumes_from_the_stored_next_link_with_an_exact_final_count()
    {
        using var t = new TempIndex();
        var drive = ThreePageDrive();
        drive.FailAfterPages = 2;
        await Assert.ThrowsAsync<IOException>(() => new DeltaIngestor(drive, t.Index).RunAsync(CancellationToken.None));
        Assert.Equal("next:2", t.Index.GetMeta(DeltaIngestor.NextKey));          // two pages were committed, the token points at the third
        Assert.Equal(4, t.Index.Count());

        drive.FailAfterPages = int.MaxValue;
        var result = await new DeltaIngestor(drive, t.Index).RunAsync(CancellationToken.None);
        Assert.Equal("next:2", drive.Requests[^1]);                              // resumed exactly where it stopped
        Assert.Equal(1, result.Pages);
        Assert.Equal(5, t.Index.Count());
    }

    [Fact]
    public async Task An_incremental_round_applies_updates_and_deletions_from_the_stored_delta_link()
    {
        using var t = new TempIndex();
        var drive = ThreePageDrive();
        await new DeltaIngestor(drive, t.Index).RunAsync(CancellationToken.None);

        drive.Incremental["delta:1"] = [new DeltaPage([Up(Items.File("a", "F", "a-renamed.txt", 7)), DeltaChange.Delete("b"), Up(Items.File("e", "F", "e.txt"))], null, "delta:2")];
        var result = await new DeltaIngestor(drive, t.Index).RunAsync(CancellationToken.None);
        Assert.Equal("delta:1", drive.Requests[^1]);
        Assert.Equal(5, t.Index.Count());                                        // +e, -b
        Assert.Equal("a-renamed.txt", t.Index.TryGet("a")!.Value.Name);
        Assert.Null(t.Index.TryGet("b"));
        Assert.Equal("delta:2", t.Index.GetMeta(DeltaIngestor.DeltaKey));
        Assert.Equal("delta:2", result.DeltaLink);
    }

    [Fact]
    public async Task A_410_resync_re_enumerates_everything_and_prunes_items_that_vanished_in_the_gap()
    {
        using var t = new TempIndex();
        var drive = ThreePageDrive();
        await new DeltaIngestor(drive, t.Index).RunAsync(CancellationToken.None);
        Assert.Equal(5, t.Index.Count());

        // The token expires; the new full enumeration no longer contains "c" and "d".
        drive.ExpiredTokens.Add("delta:1");
        drive.FullPages.Clear();
        drive.FullPages.Add(ScriptedDrive.Page(0, 2, Up(Items.Folder("F", "root", "Docs")), Up(Items.File("a", "F", "a.txt"))));
        drive.FullPages.Add(ScriptedDrive.Page(1, 2, Up(Items.File("b", "F", "b.txt"))));
        var result = await new DeltaIngestor(drive, t.Index).RunAsync(CancellationToken.None);

        Assert.True(result.FullResync);
        Assert.Equal(3, t.Index.Count());
        Assert.Null(t.Index.TryGet("c"));
        Assert.Null(t.Index.TryGet("d"));
        Assert.Equal(1, t.Index.Generation);
    }
}
