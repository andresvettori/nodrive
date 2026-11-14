using NoDrive.Core;
using Xunit;
using NoDrive.Graph;

namespace NoDrive.Core.Tests;

public class ScaffoldTests
{
    [Fact]
    public void ItemState_values_are_stable_for_on_disk_storage()
    {
        // The index persists ItemState as a byte; renumbering would corrupt existing databases.
        Assert.Equal(0, (byte)ItemState.RemoteOnly);
        Assert.Equal(3, (byte)ItemState.InSync);
        Assert.Equal(8, (byte)ItemState.Error);
    }

    [Fact]
    public void Default_client_id_is_a_guid()
    {
        Assert.True(Guid.TryParseExact(GraphEndpoints.DefaultClientId, "D", out _));
    }

    [Fact]
    public void Upload_fragment_alignment_is_320_KiB()
    {
        Assert.Equal(327_680, GraphEndpoints.UploadFragmentAlignment);
    }
}
