using NoDrive.Core.Sync;
using Xunit;

namespace NoDrive.Core.Tests;

/// <summary>The upload/delete safety rules extracted from the Windows-only services, tested without I/O.</summary>
public class SyncDecisionsTests
{
    private const string H = "HASH=";
    private static string Never() => throw new InvalidOperationException("the local hash must not be computed here");

    [Fact]
    public void Identical_content_under_the_name_is_linked_not_copied()
    {
        Assert.Equal(ClashAction.LinkExisting, ClashPolicy.Decide(100, true, false, 100, H, 0, () => H));
    }

    [Fact]
    public void Same_size_but_different_hash_is_a_real_conflict_after_the_waiting_attempts()
    {
        Assert.Equal(ClashAction.ConflictCopy, ClashPolicy.Decide(100, true, false, 100, "OTHER=", 0, () => H));
    }

    [Fact]
    public void A_different_size_never_computes_the_local_hash()
    {
        Assert.Equal(ClashAction.ConflictCopy, ClashPolicy.Decide(100, true, false, 99, H, 0, Never));
    }

    [Fact]
    public void An_empty_cloud_file_under_a_non_empty_local_file_is_the_leftover_of_an_interrupted_upload()
    {
        Assert.Equal(ClashAction.ClearEmptyLeftover, ClashPolicy.Decide(98_000_000, true, false, 0, "AAAAAAAAAAAAAAAAAAAAAAAAAAA=", 1, Never));
    }

    [Fact]
    public void When_the_leftover_cannot_be_removed_the_name_is_waited_for_then_conflict_copy_after_the_attempts()
    {
        Assert.Equal(ClashAction.ConflictCopy, ClashPolicy.Decide(98_000_000, true, false, 0, "AAAAAAAAAAAAAAAAAAAAAAAAAAA=", 1, Never, leftoverRemovalFailed: true));
    }

    [Fact]
    public void Nothing_visible_under_the_name_means_wait()
    {
        Assert.Equal(ClashAction.WaitForReservedName, ClashPolicy.Decide(100, false, false, 0, null, 0, Never));
        Assert.Equal(ClashAction.WaitForReservedName, ClashPolicy.Decide(100, false, false, 0, null, 99, Never));
    }

    [Fact]
    public void An_item_without_hash_is_waited_for_a_few_times_then_treated_as_a_conflict()
    {
        Assert.Equal(ClashAction.WaitForReservedName, ClashPolicy.Decide(100, true, false, 50, null, ClashPolicy.WaitAttempts - 1, Never));
        Assert.Equal(ClashAction.ConflictCopy, ClashPolicy.Decide(100, true, false, 50, null, ClashPolicy.WaitAttempts, Never));
    }

    [Fact]
    public void A_folder_under_the_name_is_never_linked_or_cleared()
    {
        Assert.Equal(ClashAction.ConflictCopy, ClashPolicy.Decide(100, true, true, 0, H, ClashPolicy.WaitAttempts, Never));
    }

    [Fact]
    public void Edit_detection_uses_size_and_a_two_second_tolerance_on_the_modified_time()
    {
        long t = TimeSpan.TicksPerSecond;
        Assert.False(ChangeDetection.IsEdited(10, 100 * t, 10, 100 * t + t));
        Assert.False(ChangeDetection.IsEdited(10, 100 * t, 10, 100 * t + 2 * t));
        Assert.True(ChangeDetection.IsEdited(10, 100 * t, 10, 100 * t + 3 * t));
        Assert.True(ChangeDetection.IsEdited(10, 100 * t, 11, 100 * t));
    }

    [Fact]
    public void A_save_without_changes_is_recognised_by_size_and_hash_and_big_files_are_not_hashed()
    {
        Assert.True(ChangeDetection.LocalEqualsCloud(10, H, 10, () => H));
        Assert.False(ChangeDetection.LocalEqualsCloud(10, H, 10, () => "X="));
        Assert.False(ChangeDetection.LocalEqualsCloud(10, null, 10, Never));                    // unknown cloud hash: no hashing, upload
        Assert.False(ChangeDetection.LocalEqualsCloud(10, H, 11, Never));                       // different size: no hashing
        Assert.False(ChangeDetection.LocalEqualsCloud(ChangeDetection.MaxHashBytes + 1, H, ChangeDetection.MaxHashBytes + 1, Never));
    }

    [Fact]
    public void A_delete_is_only_retried_when_size_and_hash_match_what_we_know()
    {
        Assert.True(ChangeDetection.SameContent(5, H, 5, H));
        Assert.False(ChangeDetection.SameContent(5, H, 6, H));
        Assert.False(ChangeDetection.SameContent(5, H, 5, "X="));
        Assert.False(ChangeDetection.SameContent(5, null, 5, null));                           // no hash on either side: not provably the same
    }

    [Theory]
    [InlineData(0, 49, false)]
    [InlineData(0, 50, false)]
    [InlineData(0, 51, true)]
    [InlineData(2000, 100, false)]
    [InlineData(2000, 101, true)]
    [InlineData(100_000, 5000, false)]
    [InlineData(100_000, 5001, true)]
    public void Missing_files_are_held_above_the_larger_of_50_and_five_percent(long local, int missing, bool held) =>
        Assert.Equal(held, MissingGuard.ShouldHold(missing, local));
}
