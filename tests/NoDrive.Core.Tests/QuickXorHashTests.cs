using Xunit;
using NoDrive.Core.Hashing;

namespace NoDrive.Core.Tests;

public class QuickXorHashTests
{
    [Fact]
    public void EmptyIsAllZero() => Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAA=", QuickXorHash.Of(new MemoryStream(), TestContext.Current.CancellationToken));

    [Fact]
    public void SameContentSameHashDifferentContentDiffers()
    {
        byte[] a = new byte[100_000]; new Random(1).NextBytes(a);
        byte[] b = (byte[])a.Clone(); b[5000] ^= 1;
        Assert.Equal(QuickXorHash.Of(new MemoryStream(a), TestContext.Current.CancellationToken), QuickXorHash.Of(new MemoryStream(a), TestContext.Current.CancellationToken));
        Assert.NotEqual(QuickXorHash.Of(new MemoryStream(a), TestContext.Current.CancellationToken), QuickXorHash.Of(new MemoryStream(b), TestContext.Current.CancellationToken));
    }
}
