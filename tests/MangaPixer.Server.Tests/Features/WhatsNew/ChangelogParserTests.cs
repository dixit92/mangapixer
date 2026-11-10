namespace com.lifepixer.mangapixer.Server.Tests.Features.WhatsNew;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.WhatsNew;
using Xunit;

/// <summary>
/// Unit tests of the "What's new" CHANGELOG parser (1.53.0) over the synthetic fixture: sections, groups, wrapped bullets, the safe inline
/// subset, app-path links only, <c>[Unreleased]</c> skipped, and a malformed file reading as no sections with an error code (never a throw).
/// One test reads the CHANGELOG embedded in the server assembly: it must be there and must parse - the release ritual's guard.
/// </summary>
public sealed class ChangelogParserTests
{
    private static ChangelogParser.Result Fixture() => ChangelogParser.Parse(WhatsNewFixture.Changelog);

    [Fact]
    public void Sections_AreTheReleasedVersions_InFileOrder_WithDates_AndUnreleasedSkipped()
    {
        var result = Fixture();

        Assert.Null(result.Error);
        Assert.Equal(["2.2.0", "2.0.1", "2.1.0", "2.0.0"], result.Sections.Select(s => s.Version));
        Assert.Equal(["2030-03-01", "2030-01-15", "2030-02-01", "2030-01-01"], result.Sections.Select(s => s.Date));
        var everyText = string.Concat(result.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Items).SelectMany(i => i.Parts).Select(p => p.Text));
        Assert.DoesNotContain("UNRELEASED", everyText, StringComparison.Ordinal);
    }

    [Fact]
    public void Groups_AndBullets_KeepFileOrder_AndWrappedLinesJoinWithOneSpace()
    {
        var top = Fixture().Sections[0];

        Assert.Equal(["Added", "Fixed"], top.Groups.Select(g => g.Name));
        Assert.Equal(2, top.Groups[0].Items.Count);
        var first = string.Concat(top.Groups[0].Items[0].Parts.Select(p => p.Text));
        Assert.Equal(
            "Shared lanterns are off until you turn them on in Administration. Each user can turn theirs off in Settings, and the lantern: search term finds them.",
            first);
        Assert.Equal("A plain item.", Assert.Single(top.Groups[0].Items[1].Parts).Text);
    }

    [Fact]
    public void Inline_KeepsBoldCodeAndAppLinks_AsFlaggedParts()
    {
        var parts = Fixture().Sections[0].Groups[0].Items[0].Parts;

        Assert.Equal(new WhatsNewInlineDto { Text = "Shared lanterns", Bold = true }, parts[0]);
        Assert.Equal(new WhatsNewInlineDto { Text = " are off until you turn them on in " }, parts[1]);
        Assert.Equal(new WhatsNewInlineDto { Text = "Administration", Link = "/admin?tab=general" }, parts[2]);
        Assert.Contains(new WhatsNewInlineDto { Text = "lantern:", Code = true }, parts);
    }

    [Fact]
    public void Links_OutsideTheApp_KeepOnlyTheirText()
    {
        var parts = Fixture().Sections[0].Groups[1].Items[0].Parts;

        Assert.All(parts, p => Assert.Null(p.Link));
        Assert.Equal(
            "Links outside the app keep their text only: the docs, a site and a host.",
            string.Concat(parts.Select(p => p.Text)));
    }

    [Theory]
    [InlineData("/admin", true)]
    [InlineData("/admin/metadata?tab=review#x", true)]
    [InlineData("/", true)]
    [InlineData("//example.org", false)]
    [InlineData("/\\example.org", false)]
    [InlineData("https://example.org", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("docs/ratings.md", false)]
    [InlineData("/a b", false)]
    [InlineData("", false)]
    public void IsAppPath_AcceptsOnlyASingleLeadingSlash(string target, bool expected) =>
        Assert.Equal(expected, ChangelogParser.IsAppPath(target));

    [Theory]
    [InlineData("a **b** c", "a |**b**| c")]
    [InlineData("an unpaired ** stays", "an unpaired ** stays")]
    [InlineData("a `x ** y` code", "a |`x ** y`| code")]
    [InlineData("an unclosed ` tick", "an unclosed ` tick")]
    [InlineData("**[bold link](/admin)** end", "**[bold link](/admin)**| end")]
    [InlineData("a [broken](link", "a [broken](link")]
    [InlineData("<b>html</b> & stays text", "<b>html</b> & stays text")]
    public void Inline_Tokens(string text, string expected) =>
        Assert.Equal(expected, string.Join("|", ChangelogParser.ParseInline(text).Select(Render)));

    private static string Render(WhatsNewInlineDto p)
    {
        var s = p.Code ? $"`{p.Text}`" : p.Text;
        if (p.Link is not null) s = $"[{s}]({p.Link})";
        return p.Bold ? $"**{s}**" : s;
    }

    [Theory]
    [InlineData("## 1.2.0 - 2030-01-01\n\n### Added\n\n- x", "section_heading")]
    [InlineData("## [one.two] - 2030-01-01\n\n### Added\n\n- x", "section_version")]
    [InlineData("## [v1.2.0] - 2030-01-01\n\n### Added\n\n- x", "section_version")]
    [InlineData("## [1.2.0] - 01/02/2030\n\n### Added\n\n- x", "section_date")]
    [InlineData("## [1.2.0] - 2030-01-01\n\n- before any group", "item_outside_group")]
    [InlineData("- before any section\n## [1.2.0] - 2030-01-01", "item_outside_section")]
    [InlineData("### Added\n\n## [1.2.0] - 2030-01-01", "group_outside_section")]
    [InlineData("## [1.2.0] - 2030-01-01\n\n###\n\n- x", "group_name")]
    [InlineData("## [1.2.0] - 2030-01-01\n\n#### Deeper\n\n- x", "heading")]
    [InlineData("## [1.2.0] - 2030-01-01\n\n### Added\n\n- x\n\n## [1.2.0+sha.b] - 2030-01-02\n\n### Fixed\n\n- y", "duplicate_version")]
    [InlineData("", "empty")]
    [InlineData("   \n", "empty")]
    public void Malformed_File_YieldsNoSections_AndAnErrorCode(string text, string error)
    {
        var result = ChangelogParser.Parse(text);

        Assert.Empty(result.Sections);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void A_MalformedHeading_AnywhereInTheFile_ShowsNothing_NotHalfTheNotes() =>
        Assert.Empty(ChangelogParser.Parse(WhatsNewFixture.Changelog + "\n## Not a version\n").Sections);

    [Fact]
    public void A_HeadingWithoutDate_AndCrlfLineEnds_Parse()
    {
        var result = ChangelogParser.Parse("# Changelog\r\n\r\n## [3.0.0]\r\n\r\n### Added\r\n\r\n- one\r\n  two\r\n");

        Assert.Null(result.Error);
        var section = Assert.Single(result.Sections);
        Assert.Null(section.Date);
        Assert.Equal("one two", Assert.Single(Assert.Single(Assert.Single(section.Groups).Items).Parts).Text);
    }

    [Fact]
    public void Unreleased_IsSkipped_WhateverItHolds()
    {
        var result = ChangelogParser.Parse("## [Unreleased]\n\n#### anything\n- loose\n\n## [1.0.0] - 2030-01-01\n\n### Added\n\n- shipped");

        Assert.Null(result.Error);
        Assert.Equal("shipped", Assert.Single(Assert.Single(Assert.Single(Assert.Single(result.Sections).Groups).Items).Parts).Text);
    }

    [Fact]
    public void ParagraphText_InsideAGroup_IsNotShown_AndEndsTheItem()
    {
        var result = ChangelogParser.Parse("## [1.0.0] - 2030-01-01\n\n### Added\n\n- one\nnot indented\n- two");

        Assert.Equal(["one", "two"], Assert.Single(Assert.Single(result.Sections).Groups).Items.Select(i => i.Parts[0].Text));
    }

    /// <summary>The server's own CHANGELOG is embedded and reads without an error - a malformed release section fails here, not on an admin's screen.</summary>
    [Fact]
    public void TheEmbeddedChangelog_IsPresent_AndParses()
    {
        var text = WhatsNewNotes.ReadEmbedded();

        Assert.NotNull(text);
        var result = ChangelogParser.Parse(text);
        Assert.Null(result.Error);
        Assert.NotEmpty(result.Sections);
        Assert.All(result.Sections, s => Assert.All(s.Groups, g => Assert.NotEmpty(g.Items)));
    }
}
