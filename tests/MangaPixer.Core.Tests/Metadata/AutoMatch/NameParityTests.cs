namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using Xunit;

/// <summary>
/// Name parsing parity with MangaList (1.42.0): the general conventions behind MangaList's 2026-10-11 archive-name fixes - a trailing site
/// watermark, a three-decimal / padded sub-number, a placeholder group, and a chapter number right after the series head that the folder's
/// siblings vary (a number they all share is part of the title: Mihon's rule). Synthetic names, and public titles that end in a number as the
/// negative cases.
/// </summary>
public sealed class NameParityTests
{
    private static decimal? D(string s) => s.Length == 0 ? null : decimal.Parse(s, CultureInfo.InvariantCulture);

    private static UnitNumbers U(string volume, string chapter, bool extra = false, string chapterEnd = "") =>
        new(D(volume), null, D(chapter), D(chapterEnd), extra);

    private static GroupingRow Archive(string name, string? container = null, int? ciVolume = null) =>
        new("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant(), container, ciVolume);

    private static List<UnitNumbers> Read(params string[] names) => ReadIn(null, names);

    private static List<UnitNumbers> ReadIn(string? container, params string[] names) =>
        VolumeGrouping.WithSeriesHeads(names.Select(n => Archive(n, container)).ToList()).Select(VolumeGrouping.UnitsOf).ToList();

    // --- A. a site's watermark at the end of a name ------------------------------------------------------------------------

    [Theory]
    [InlineData("Some Series Read Online", "Some Series")]
    [InlineData("Some Series - Read Manga Online Free", "Some Series")]
    [InlineData("Some Series Online Free", "Some Series")]
    [InlineData("Some Series Read Free.cbz", "Some Series")]
    [InlineData("Some Series Read Manhwa Online", "Some Series")]
    [InlineData("Some Series Read Online for Free", "Some Series")]
    [InlineData("Some Series Free Online", "Some Series")]
    [InlineData("Some_Series_Read_Online", "Some Series")]
    public void Watermark_IsNotPartOfTheSearchText(string name, string expected) =>
        Assert.Equal(expected, TitleNormalizer.Normalize(name).Primary);

    [Theory]
    [InlineData("Sword Art Online")] // one watermark word is never enough
    [InlineData("Some Series Online")]
    [InlineData("Some Series Manga Online")]
    [InlineData("Some Series Free")]
    [InlineData("Read or Die")]
    [InlineData("Online Free")] // nothing before it: the whole name is the title
    [InlineData("Free Online Stories")] // not at the end
    public void Watermark_NeverEatsATitle(string name) =>
        Assert.Equal(name, TitleNormalizer.Normalize(name).Primary);

    [Theory]
    [InlineData("Some Series 001 Read Online.cbz", "Some Series")]
    [InlineData("Some Series 012 - Read Manga Online Free.cbz", "Some Series")]
    public void Watermark_IsNotPartOfTheBaseTitle(string name, string expected) =>
        Assert.Equal(expected, TitleNormalizer.ArchiveBaseTitle(name));

    [Fact]
    public void Watermark_HidesNoBareChapterNumber()
    {
        Assert.Equal(U("", "1"), AutoMatchText.UnitsOf("001 Read Online.cbz"));
        Assert.Equal(U("", "5"), AutoMatchText.UnitsOf("Some Series - Some Series Ch.005 Read Online.cbz")); // as before
    }

    // --- B. three decimals; a padded sub-number ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("Some Series Ch.014.005.cbz", "14.5")] // padded to three digits like the number before it: .5
    [InlineData("Some Series - Some Series Ch.021.005 Read Online.cbz", "21.5")]
    [InlineData("Some Series Ch.021.010.cbz", "21.1")]
    [InlineData("Some Series Ch.014.125.cbz", "14.125")] // three digits not starting with 0: as written
    [InlineData("Some Series c014.25.cbz", "14.25")]
    [InlineData("Some Series Ch.014.5.cbz", "14.5")] // as before
    [InlineData("0015 [0014.005 - A Title].cbz", "14.5")]
    public void ThreeDecimals_AreKept_NotTruncatedToTheWholeChapter(string name, string chapter) =>
        Assert.Equal(U("", chapter, extra: true), AutoMatchText.UnitsOf(name));

    [Fact]
    public void ThreeDecimals_ZeroFractionIsTheWholeChapter_AndTheMatcherIntegersStay()
    {
        Assert.Equal(U("", "14"), AutoMatchText.UnitsOf("Some Series Ch.014.000.cbz"));
        Assert.Equal(14, AutoMatchText.ChapterNumberOf("Some Series Ch.014.005.cbz"));
        Assert.Equal(new UnitNumbers(2.5m, null, null, null, true), AutoMatchText.UnitsOf("Some Series v02.005.cbz"));
    }

    // --- C. placeholder groups ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("no group", true)]
    [InlineData("No Group", true)]
    [InlineData("no-group", true)]
    [InlineData("No Groups", true)]
    [InlineData("no scanlator", true)]
    [InlineData("Unknown", true)]
    [InlineData("unknown group", true)]
    [InlineData("N/A", true)]
    [InlineData("none", true)]
    [InlineData("unofficial", true)]
    [InlineData("Unofficial", true)]
    [InlineData("No Group Scans", false)]
    [InlineData("Unofficial Scans", false)]
    [InlineData("Nobody", false)]
    [InlineData("The None Team", false)]
    [InlineData("Official", false)]
    public void PlaceholderGroups_AreReleaseTags(string tag, bool expected) =>
        Assert.Equal(expected, ArchiveNameAnatomy.IsReleaseTag(tag));

    [Fact]
    public void PlaceholderGroups_AreNoCreatorAndNoParody()
    {
        Assert.Empty(AutoMatchText.CreatorHints("Some Title [no group].cbz"));
        Assert.Empty(AutoMatchText.CreatorHints("Some Title (Unofficial).cbz"));
        var a = ArchiveNameAnatomy.Parse("[Some Circle] Some Title (Unofficial).cbz");
        Assert.Null(a.Parody);
        Assert.False(a.IsDoujinShaped);
    }

    // --- D. a number right after the series head (sibling context) ---------------------------------------------------------------

    [Fact]
    public void SeriesHead_IsTheTextBeforeANumberTheSiblingsVary()
    {
        Assert.Equal("some series", AutoMatchText.SeriesHeadOf(["Some Series 099 Vol 10 A Title", "Some Series 100 Vol 11 B Title", "Some Series 101 Vol 11"]));
        Assert.Equal("mob psycho 100", AutoMatchText.SeriesHeadOf(["Mob Psycho 100 001 A Title", "Mob Psycho 100 002 B Title"]));
        Assert.Equal("some series", AutoMatchText.SeriesHeadOf(["[Group] Some Series 001 Read Online.cbz", "Some Series 002 Read Online.cbz"]));
    }

    [Fact]
    public void SeriesHead_IsNone_WhenEverySiblingSharesTheNumber_OrForOneFile()
    {
        Assert.Null(AutoMatchText.SeriesHeadOf(["Cyborg 009 v01", "Cyborg 009 v02", "Cyborg 009 v03"]));
        Assert.Null(AutoMatchText.SeriesHeadOf(["Mob Psycho 100 Vol 1", "Mob Psycho 100 Vol 2"]));
        Assert.Null(AutoMatchText.SeriesHeadOf(["Some Series 2 v01", "Some Series 2 v02", "Some Series 3 v01", "Some Series 3 v02"])); // sequels
        Assert.Null(AutoMatchText.SeriesHeadOf(["Some Series 100 Vol 11 A Title"]));
        Assert.Null(AutoMatchText.SeriesHeadOf([]));
    }

    [Fact]
    public void AChapterNumberBeforeItsVolume_IsThatChapterOfTheVolume()
    {
        // MangaList shape 1 (owner, 2026-10-11): chapter 100 onwards is not zero-padded.
        Assert.Equal(
            [U("10", "99"), U("11", "100"), U("11", "101"), U("11", "102"), U("11", "12.5", extra: true), U("103", "1045")],
            Read("Some Series 099 Vol 10 A Title", "Some Series 100 Vol 11 Memories of the Sea", "Some Series 101 Vol 11 Another Title",
                "Some Series 102 Vol 11", "Some Series 012.5 Vol 11", "Some Series 1045 Vol 103 A Title"));
    }

    [Fact]
    public void ABareChapterNumberAfterTheSeriesHead_IsTheChapter_WhateverItsTitleEndsIn()
    {
        // MangaList shapes 2 and 5: "014.5  <title>, Part 2" is chapter 14.5 (never 2), "002 <title>" is chapter 2.
        Assert.Equal(
            [U("", "14.5", extra: true), U("", "38.1", extra: true), U("", "64"), U("", "2"), U("", "1.1", extra: true), U("", "120")],
            Read("Some Series 014.5  Baseball, That Is, A Game of Life and Death, Part 2", "Some Series 038.1  The New President Part 1",
                "Some Series 064  After The Truth", "Some Series 002 Searching for Dawn", "Some Series 001.1 Hero and Villain",
                "Some Series 120 (2023) (Digital) (Some Group)"));
    }

    [Fact]
    public void AWatermarkedBareNumber_IsReadAfterTheSeriesHead()
    {
        Assert.Equal([U("", "1"), U("", "2"), U("", "3")],
            Read("Some Series 001 Read Online", "Some Series 002 Read Online", "Some Series - 003 - Read Manga Online Free"));
    }

    [Fact]
    public void ATitleThatEndsInANumber_StaysTheTitle()
    {
        Assert.Equal([U("1", ""), U("2", ""), U("3", "")], Read("Cyborg 009 v01", "Cyborg 009 v02", "Cyborg 009 Vol 3"));
        Assert.Equal([U("1", ""), U("2", "")], Read("Mob Psycho 100 v01 (2018) (Digital)", "Mob Psycho 100 v02 (2018) (Digital)"));
        Assert.Equal([U("1", ""), U("2", "")], Read("Mobile Suit Gundam 0079 Vol 1", "Mobile Suit Gundam 0079 Vol 2"));
        Assert.Equal([U("1", ""), U("2", "")], Read("Space Battleship Yamato 2199 v01", "Space Battleship Yamato 2199 v02"));
        Assert.Equal([U("", "1"), U("", "2")], Read("Mob Psycho 100 001 A Title", "Mob Psycho 100 002 B Title"));
    }

    [Fact]
    public void ShortOrYearNumbers_AndLoneFiles_AreNotRead()
    {
        Assert.Equal([U("", ""), U("", "")], Read("Some Series 07", "Some Series 08")); // volumes or chapters? not said
        Assert.Equal([U("3", ""), U("4", "")], Read("Some Series 2 Vol 3 Subtitle", "Some Series 5 Vol 4 Subtitle"));
        Assert.Equal([U("", ""), U("", "")], Read("Some Series 2019 A Title", "Some Series 2020 B Title"));
        Assert.Equal([U("", "")], Read("Some Series 002 Searching for Dawn"));
        Assert.Equal(AutoMatchText.UnitsOf("Some Series 002 A Title"), AutoMatchText.UnitsOf("Some Series 002 A Title", null));
    }

    [Fact]
    public void InAVolumesFolder_OrWhenComicInfoSaysSo_TheNumberIsAVolume()
    {
        Assert.Equal([U("1", ""), U("2", "")], ReadIn("Volumes", "Some Series 001", "Some Series 002"));
        var rows = VolumeGrouping.WithSeriesHeads([Archive("Some Series 005", ciVolume: 5), Archive("Some Series 006", ciVolume: 6)]);
        Assert.Equal([U("5", ""), U("6", "")], rows.Select(VolumeGrouping.UnitsOf));
    }

    [Fact]
    public void TheVolumesView_StacksChaptersReadAfterTheSeriesHead()
    {
        var rows = new[] { "Some Series 099 Vol 10 A", "Some Series 100 Vol 11 B", "Some Series 101 Vol 11 C" }.Select(n => Archive(n)).ToList();
        var r = VolumeGrouping.Group(rows, null);
        var stacks = r.Entries.Where(e => e.Kind == VolumeEntryKind.Stack).Select(e => e.Stack!).ToList();
        Assert.Equal(["10", "11"], stacks.Select(s => s.Key));
        Assert.Equal(["id:Some Series 099 Vol 10 A", "id:Some Series 100 Vol 11 B", "id:Some Series 101 Vol 11 C"],
            r.Entries.SelectMany(e => e.Stack?.Members.Select(m => m.Row.Id) ?? []).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheMissingReport_ReadsTheSameChapters()
    {
        var result = MissingUnits.Evaluate([["Some Series 001 A", "Some Series 002 B", "Some Series 004 D"]], new PublishedTotals());
        Assert.NotNull(result.Chapters);
        Assert.Equal(1, result.Chapters!.MissingCount);
    }
}
