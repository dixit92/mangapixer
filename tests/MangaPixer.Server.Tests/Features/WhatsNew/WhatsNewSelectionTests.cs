namespace com.lifepixer.mangapixer.Server.Tests.Features.WhatsNew;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.WhatsNew;
using Xunit;

/// <summary>
/// Unit tests of which sections an admin sees (1.53.0): newer than the last seen version, not newer than the running one, newest first;
/// null (or unreadable) last seen = the running version's section only; <c>currentOnly</c> = the running version's section; versions
/// compare by their core - build metadata and a pre-release label ignored.
/// </summary>
public sealed class WhatsNewSelectionTests
{
    private static readonly IReadOnlyList<WhatsNewSectionDto> Sections = ChangelogParser.Parse(WhatsNewFixture.Changelog).Sections;

    private static string[] Select(string? running, string? lastSeen, bool currentOnly = false) =>
        WhatsNewSelection.Select(Sections, running, lastSeen, currentOnly).Select(s => s.Version).ToArray();

    [Theory]
    [InlineData("2.0.0", new[] { "2.2.0", "2.1.0", "2.0.1" })]   // older: everything since, newest first (file order was 2.2.0, 2.0.1, 2.1.0)
    [InlineData("2.0.1", new[] { "2.2.0", "2.1.0" })]
    [InlineData("2.1.0", new[] { "2.2.0" })]
    [InlineData("2.2.0", new string[0])]                          // equal: nothing new
    [InlineData("2.3.0", new string[0])]                          // newer than running (a downgrade): nothing
    [InlineData("1.0.0", new[] { "2.2.0", "2.1.0", "2.0.1", "2.0.0" })]
    public void SinceTheLastSeenVersion_UpToTheRunningOne(string lastSeen, string[] expected) =>
        Assert.Equal(expected, Select("2.2.0", lastSeen));

    [Fact]
    public void NotNewerThanTheRunningVersion() =>
        Assert.Equal(["2.1.0", "2.0.1"], Select("2.1.0", "2.0.0"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a version")]
    public void NeverSeen_ShowsTheRunningVersionsSectionOnly(string? lastSeen) =>
        Assert.Equal(["2.1.0"], Select("2.1.0", lastSeen));

    [Fact]
    public void NeverSeen_AndNoSectionForTheRunningVersion_ShowsNothing() =>
        Assert.Empty(Select("2.5.0", null));

    [Theory]
    [InlineData("2.2.0+sha.abc1234", "2.1.0")]
    [InlineData("2.2.0-rc.1", "2.1.0")]
    [InlineData("v2.2.0-rc.1+sha.abc", "2.1.0+sha.old")]
    [InlineData("2.2.0", "2.1.0-beta.2")]
    public void BuildMetadata_AndPreRelease_AreIgnored(string running, string lastSeen) =>
        Assert.Equal(["2.2.0"], Select(running, lastSeen));

    [Theory]
    [InlineData("2.2.0", "2.2.0+sha.other")]
    [InlineData("2.2.0-rc.1", "2.2.0")]
    public void SameCore_IsSeen(string running, string lastSeen) =>
        Assert.Empty(Select(running, lastSeen));

    [Theory]
    [InlineData(null)]
    [InlineData("2.0.0")]
    [InlineData("2.2.0")]
    public void CurrentOnly_IsTheRunningVersionsSection_SeenOrNot(string? lastSeen) =>
        Assert.Equal(["2.0.1"], Select("2.0.1", lastSeen, currentOnly: true));

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    [InlineData("")]
    public void AnUnreadableRunningVersion_ShowsNothing(string? running)
    {
        Assert.Empty(Select(running, null));
        Assert.Empty(Select(running, "1.0.0"));
        Assert.Empty(Select(running, null, currentOnly: true));
    }

    [Theory]
    [InlineData("1.53.0+sha.d1aa9ff", "1.53.0")]
    [InlineData("1.53.0-rc.2", "1.53.0")]
    [InlineData("v2", "2.0.0")]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void CoreText_IsMajorMinorPatch(string? version, string? expected) =>
        Assert.Equal(expected, WhatsNewSelection.CoreText(version));
}
