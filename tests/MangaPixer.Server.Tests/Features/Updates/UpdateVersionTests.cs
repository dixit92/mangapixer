namespace com.lifepixer.mangapixer.Server.Tests.Features.Updates;

using com.lifepixer.mangapixer.Server.Features.Updates;
using Xunit;

/// <summary>
/// Unit tests for the pure version comparison used by the Update Checker.
/// Covers the cases called out in the lane spec: v-prefix normalization,
/// pre-release ordering, and equal/newer/older core comparisons — plus
/// build-metadata stripping and malformed input.
/// </summary>
public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.21.0", 1, 21, 0, null)]
    [InlineData("1.21.0", 1, 21, 0, null)]
    [InlineData("V2.0.0", 2, 0, 0, null)]
    [InlineData("1.21.0-rc.1", 1, 21, 0, "rc.1")]
    [InlineData("v1.21.0+sha.abc123", 1, 21, 0, null)]
    [InlineData("1.21.0-rc.1+build.5", 1, 21, 0, "rc.1")]
    [InlineData("2", 2, 0, 0, null)]
    [InlineData("2.3", 2, 3, 0, null)]
    public void TryParse_normalizes_prefix_prerelease_and_metadata(
        string raw, int major, int minor, int patch, string? pre)
    {
        var parsed = UpdateVersion.TryParse(raw);

        Assert.NotNull(parsed);
        Assert.Equal(major, parsed!.Value.Major);
        Assert.Equal(minor, parsed.Value.Minor);
        Assert.Equal(patch, parsed.Value.Patch);
        Assert.Equal(pre, parsed.Value.PreRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("1.2.3.4")]
    [InlineData("v.beta")]
    public void TryParse_returns_null_for_malformed_input(string? raw)
    {
        Assert.Null(UpdateVersion.TryParse(raw));
    }

    [Theory]
    // newer
    [InlineData("v1.22.0", "1.21.0", true)]
    [InlineData("1.21.1", "1.21.0", true)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.21.0", "1.21.0-rc.1", true)]   // release beats its own pre-release
    [InlineData("1.21.0-rc.2", "1.21.0-rc.1", true)]
    // equal
    [InlineData("1.21.0", "1.21.0", false)]
    [InlineData("v1.21.0+sha.a", "1.21.0+sha.b", false)]  // build metadata ignored
    // older
    [InlineData("1.20.0", "1.21.0", false)]
    [InlineData("1.21.0-rc.1", "1.21.0", false)]   // pre-release is older than release
    [InlineData("1.21.0-rc.1", "1.21.0-rc.2", false)]
    public void IsNewer_orders_versions(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateVersion.IsNewer(candidate, current));
    }

    [Theory]
    [InlineData(null, "1.21.0")]
    [InlineData("1.21.0", null)]
    [InlineData("garbage", "1.21.0")]
    [InlineData("1.21.0", "garbage")]
    public void IsNewer_is_false_when_either_side_is_unparseable(string? candidate, string? current)
    {
        Assert.False(UpdateVersion.IsNewer(candidate, current));
    }
}
