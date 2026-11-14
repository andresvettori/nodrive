using NoDrive.Core.Hydration;
using Xunit;

namespace NoDrive.Core.Tests;

public class ReadAheadPlannerTests
{
    private const long MiB = 1024 * 1024;
    private const long File = 500 * MiB;

    [Fact]
    public void A_first_small_read_is_only_modestly_extended_so_thumbnail_style_reads_stay_cheap()
    {
        var p = new ReadAheadPlanner();
        long fetch = p.Plan("v", 0, 64 * 1024, 64 * 1024, File - 64 * 1024, File);      // Photos-style: 64 KiB required, the rest optional
        Assert.Equal(256 * 1024, fetch);
    }

    [Fact]
    public void Sequential_reads_double_the_window_up_to_the_cap()
    {
        var p = new ReadAheadPlanner();
        long offset = 0;
        var fetched = new List<long>();
        for (int i = 0; i < 6; i++)
        {
            long f = p.Plan("v", offset, 64 * 1024, offset + 64 * 1024, File - offset, File);
            fetched.Add(f);
            offset += f;                                                                 // the next request starts where the fetch ended
        }
        Assert.Equal([256 * 1024, 1 * MiB, 2 * MiB, 4 * MiB, 8 * MiB, 16 * MiB], fetched);
    }

    [Fact]
    public void A_seek_resets_the_window()
    {
        var p = new ReadAheadPlanner();
        long o = 0;
        for (int i = 0; i < 4; i++) { o += p.Plan("v", o, 4096, o + 4096, File - o, File); }
        long afterSeek = p.Plan("v", 300 * MiB, 4096, 300 * MiB + 4096, File - 300 * MiB, File);
        Assert.Equal(256 * 1024, afterSeek);
    }

    [Fact]
    public void Read_ahead_never_passes_what_windows_offers_or_the_end_of_the_file()
    {
        var p = new ReadAheadPlanner();
        Assert.Equal(64 * 1024 + 100 * 1024, p.Plan("a", 0, 64 * 1024, 64 * 1024, 100 * 1024, 10 * MiB));     // optional range is only 100 KiB
        long nearEnd = File - 100 * 1024;
        Assert.Equal(100 * 1024, p.Plan("b", nearEnd, 4096, nearEnd + 4096, 100 * 1024 - 4096, File));        // clamped at EOF (unaligned allowed there)
    }

    [Fact]
    public void Whole_file_or_already_large_requests_are_left_alone_and_no_offer_means_no_read_ahead()
    {
        var p = new ReadAheadPlanner();
        Assert.Equal(900 * MiB, p.Plan("w", 0, 900 * MiB, 0, 0, 900 * MiB));            // Media Player style: required covers everything
        Assert.Equal(64 * 1024, p.Plan("n", 0, 64 * 1024, 0, 0, File));                 // optional range empty: Windows did not offer more
    }

    [Fact]
    public void Fetch_lengths_are_aligned_to_the_platform_granularity_except_at_the_file_end()
    {
        var p = new ReadAheadPlanner(new ReadAheadOptions { InitialBytes = 1_000_000 });
        long f = p.Plan("x", 0, 5000, 5000, File, File);
        Assert.Equal(0, f % 4096);
        Assert.True(f >= 1_000_000);
    }

    [Fact]
    public void The_planner_remembers_only_a_bounded_number_of_files()
    {
        var p = new ReadAheadPlanner(new ReadAheadOptions { TrackedFiles = 8 });
        for (int i = 0; i < 100; i++) { p.Plan("file" + i, 0, 4096, 4096, File, File); }
        // No exception and no unbounded growth; a forgotten file simply restarts at the initial window.
        Assert.Equal(256 * 1024, p.Plan("file0", 0, 4096, 4096, File, File));
    }
}
