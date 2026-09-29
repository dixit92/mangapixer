namespace com.lifepixer.mangapixer.Tests.Core.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using Xunit;

/// <summary>Unit tests for the opaque id of a virtual volume stack browse entry.</summary>
public sealed class VolumeStackIdTests
{
    [Theory]
    [InlineData("k3j2h1", "3")]
    [InlineData("k3j2h1", "2.5")]
    [InlineData("a", "0")]
    public void AnIdRoundTrips_EvenWhenTheKeyHoldsADot(string folder, string key)
    {
        var id = VolumeStackId.Encode(folder, key);

        Assert.StartsWith("vs.", id);
        Assert.True(VolumeStackId.TryDecode(id, out var f, out var k));
        Assert.Equal((folder, key), (f, k));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("k3j2h1")]
    [InlineData("vs.")]
    [InlineData("vs.folder")]
    [InlineData("vs..3")]
    [InlineData("vs.folder.")]
    public void ANonStackId_IsRejected(string? id)
    {
        Assert.False(VolumeStackId.TryDecode(id, out var folder, out var key));
        Assert.Equal((string.Empty, string.Empty), (folder, key));
    }
}
