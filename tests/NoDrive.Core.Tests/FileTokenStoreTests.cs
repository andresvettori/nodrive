using NoDrive.Graph.Auth;
using Xunit;

namespace NoDrive.Core.Tests;

public class FileTokenStoreTests
{
    [Fact]
    public async Task Token_round_trips_is_replaced_and_cleared_with_owner_only_permissions()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nodrive-tok-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "sub", "token");
        try
        {
            var store = new FileTokenStore(path);
            Assert.Null(await store.LoadRefreshTokenAsync(CancellationToken.None));
            await store.SaveRefreshTokenAsync("first", CancellationToken.None);
            await store.SaveRefreshTokenAsync("second", CancellationToken.None);
            Assert.Equal("second", await store.LoadRefreshTokenAsync(CancellationToken.None));
            if (!OperatingSystem.IsWindows()) { Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path)); }
            Assert.False(File.Exists(path + ".tmp"));
            await store.ClearAsync(CancellationToken.None);
            Assert.Null(await store.LoadRefreshTokenAsync(CancellationToken.None));
        }
        finally { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } }
    }
}
