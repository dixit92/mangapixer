namespace com.lifepixer.mangapixer.Tests.Core.Metadata.Wikipedia;

using com.lifepixer.mangapixer.Core.Metadata.Wikipedia;
using Xunit;

/// <summary>
/// Unit tests for the "List of ... chapters" wikitext reader (1.32.0). The wikitext is SYNTHETIC: it reproduces the structures seen on
/// the real pages (templates, list forms, references inside values, placeholders, several tables, hub pages) with made-up titles, so no
/// Wikipedia text is stored in the repository.
/// </summary>
public sealed class WikipediaChapterListParserTests
{
    private const string Header = "{{Graphic novel list/header|Language=Japanese|LineColor=D20F24}}\n";
    private const string Footer = "{{Graphic novel list/footer}}\n";

    private static string Row(string volume, string chapters, string extra = "") =>
        "{{Graphic novel list\n| VolumeNumber    = " + volume + "\n| OriginalRelDate = January 5, 2020<ref>{{cite web|url=http://example.invalid/x|publisher=[[X]]}}</ref>\n"
        + "| OriginalISBN    = 978-4-09-850180-9\n" + extra + chapters + "\n| Summary = Made-up summary text.\n}}\n";

    private static WikipediaTable Single(string wikitext)
    {
        var page = WikipediaChapterListParser.Parse(wikitext);
        return Assert.Single(page.Tables);
    }

    private static string Chapters(WikipediaTable table, string volume) =>
        string.Join(",", table.Volumes.Single(v => v.Volume == volume).Chapters);

    [Fact]
    public void NumberedListTemplate_UsesTheStartParameter_AcrossTwoColumns()
    {
        var wikitext = Header
            + Row("1", "| ChapterList =\n{{Numbered list|start=1\n|{{nihongo|\"One\"|a|b}}\n|{{nihongo|\"Two\"|a|b}}\n}}\n| ChapterListCol2 =\n{{Numbered list|start=3\n|{{nihongo|\"Three\"|a|b}}\n}}")
            + Row("2", "| ChapterList =\n{{Numbered list|start=4\n|{{nihongo|\"Four\"|a|b}}\n|{{nihongo|\"Five\"|a|b}}\n}}")
            + Footer;
        var table = Single(wikitext);
        Assert.Equal("1,2,3", Chapters(table, "1"));
        Assert.Equal("4,5", Chapters(table, "2"));
        Assert.Equal(0, table.ImplicitChapters);
    }

    [Fact]
    public void InvokeListForm_IsReadLikeANumberedList()
    {
        var table = Single(Header + Row("1", "| ChapterList = {{#invoke:list|ordered|start=7|A|B|style=x}}") + Footer);
        Assert.Equal("7,8", Chapters(table, "1"));
    }

    [Fact]
    public void BulletsWithExplicitNumbers_AreReadWhateverTheirPrefix()
    {
        var table = Single(Header
            + Row("1", "| ChapterList =\n* 001. {{Nihongo|\"A\"|x|y}}\n* 2. {{Nihongo|\"B\"|x|y}}\n* Chapter 003: A title\n* Mission: 4 - Another")
            + Footer);
        Assert.Equal("1,2,3,4", Chapters(table, "1"));
    }

    [Fact]
    public void Ranges_AreExpanded_IncludingAFractionalEnd()
    {
        var table = Single(Header
            + Row("1", "| ChapterList =\n* Chapters 1-4\n* Mission: 5-6.1")
            + Footer);
        Assert.Equal("1,2,3,4,5,6,6.1", Chapters(table, "1"));
    }

    [Fact]
    public void RawOrderedListBlock_UsesStartAndValue()
    {
        var table = Single(Header
            + Row("1", "| ChapterList = <ol start=\"10\"><li>One</li><li>Two</li><li value=\"20\">Three</li></ol>")
            + Footer);
        Assert.Equal("10,11,20", Chapters(table, "1"));
    }

    [Fact]
    public void UnnumberedLists_AreCountedOn_AndFlagged()
    {
        var table = Single(Header
            + Row("1", "| ChapterList =\n# {{nihongo|\"A\"|x|y}}\n# {{nihongo|\"B\"|x|y}}")
            + Row("2", "| ChapterList =\n# {{nihongo|\"C\"|x|y}}\n# {{nihongo|\"D\"|x|y}}\n# {{nihongo|\"E\"|x|y}}")
            + Footer);
        Assert.Equal("1,2", Chapters(table, "1"));
        Assert.Equal("3,4,5", Chapters(table, "2"));
        Assert.Equal(5, table.ImplicitChapters);
    }

    [Fact]
    public void Specials_AreNeverPlaced()
    {
        var table = Single(Header
            + Row("1", "| ChapterList =\n* 1. First\n* Bonus. Extra page\n* Short Mission: 19\n* 2. Second")
            + Footer);
        Assert.Equal("1,2", Chapters(table, "1"));
        Assert.Equal(2, table.Specials);
    }

    [Fact]
    public void RawTableRows_AfterTheTemplate_AreTheChapterList()
    {
        var wikitext = Header
            + "{{Graphic novel list\n | VolumeNumber    = 1\n | OriginalRelDate = 27 August 2003\n}}\n|-\n| colspan=\"3\" |'''Chapter list:'''\n# {{nihongo|\"A\"|x|y}}\n# {{nihongo|\"B\"|x|y}}\n| colspan=\"2\" |'''Story date:'''\n# 19 July\n# 20 July\n|-\n"
            + "{{Graphic novel list\n | VolumeNumber    = 2\n}}\n|-\n| colspan=\"3\" |'''Chapter list:'''\n# {{nihongo|\"C\"|x|y}}\n"
            + Footer;
        var table = Single(wikitext);
        Assert.Equal("1,2", Chapters(table, "1"));
        Assert.Equal("3", Chapters(table, "2"));
    }

    [Fact]
    public void EnglishDateAndIsbn_AreTakenFromTheLicensedFields_RefsDropped()
    {
        var table = Single(Header
            + Row("1", "| ChapterList = {{Numbered list|start=1|A}}",
                "| LicensedRelDate = November 9, 2021<ref name=\"V\">{{cite web|title=X|url=http://example.invalid}}</ref>\n| LicensedISBN = 978-1-9747-2576-2\n")
            + Row("2", "| ChapterList = {{Numbered list|start=2|B}}", "| LicensedRelDate = —\n| LicensedISBN = \n")
            + Footer);
        var one = table.Volumes[0];
        Assert.Equal("2021-11-09", one.EnglishDate);
        Assert.Equal("9781974725762", one.EnglishIsbn);
        Assert.Null(table.Volumes[1].EnglishDate);
        Assert.Null(table.Volumes[1].EnglishIsbn);
    }

    [Fact]
    public void AnAnnouncedFutureDate_IsKeptAsWritten()
    {
        var table = Single(Header + Row("15", "| ChapterList = {{Numbered list|start=130|A}}", "| LicensedRelDate = December 8, 2099\n") + Footer);
        Assert.Equal("2099-12-08", table.Volumes[0].EnglishDate);
    }

    [Fact]
    public void ARowWithNoChapters_IsAnAnnouncedVolume_NotAFailure()
    {
        var table = Single(Header + Row("1", "| ChapterList = {{Numbered list|start=1|A}}") + Row("2", "") + Footer);
        Assert.Empty(table.Volumes.Single(v => v.Volume == "2").Chapters);
    }

    [Fact]
    public void ANonNumericVolume_IsSkipped()
    {
        var table = Single(Header + Row("Special edition", "| ChapterList = {{Numbered list|start=1|A}}") + Row("1", "| ChapterList = {{Numbered list|start=1|A}}") + Footer);
        Assert.Equal(["1"], table.Volumes.Select(v => v.Volume));
    }

    [Fact]
    public void ChaptersNotYetInAVolume_AreListedFromTheirSection()
    {
        var wikitext = "== Volumes ==\n" + Header + Row("1", "| ChapterList = {{Numbered list|start=1|A|B}}") + Footer
            + "== Chapters not yet in tankōbon format ==\n{{Numbered list|start=3|C|D}}\n== References ==\n";
        var table = Single(wikitext);
        Assert.Equal(["3", "4"], table.NotInVolume);
    }

    [Fact]
    public void SeveralTables_AreReturnedSeparately_WithTheirHeadings()
    {
        var wikitext = "== Main series ==\n" + Header + Row("1", "| ChapterList = {{Numbered list|start=1|A}}") + Footer
            + "== Spin-off ==\n" + Header + Row("1", "| ChapterList = {{Numbered list|start=1|X|Y|Z}}") + Footer;
        var page = WikipediaChapterListParser.Parse(wikitext);
        Assert.Equal(2, page.Tables.Count);
        Assert.Equal("Main series", page.Tables[0].Heading);
        Assert.Equal("Spin-off", page.Tables[1].Heading);
        Assert.Single(page.Tables[0].Volumes[0].Chapters);
        Assert.Equal(3, page.Tables[1].Volumes[0].Chapters.Count);
    }

    [Fact]
    public void AHubPage_ListsItsRangePages()
    {
        var page = WikipediaChapterListParser.Parse(
            "The list is split.\n* [[List of Made Up chapters (1–186)|Chapters 1–186]]\n* [[List of Made Up chapters (187–388)]]\n* [[Something else]]\n");
        Assert.Empty(page.Tables);
        Assert.Equal(["List of Made Up chapters (1–186)", "List of Made Up chapters (187–388)"], page.SubPageTitles);
    }

    [Fact]
    public void NoGraphicNovelTemplate_YieldsNoTables()
    {
        Assert.Empty(WikipediaChapterListParser.Parse("Just an article about a series.\n== Plot ==\nText.").Tables);
    }

    [Fact]
    public void UnbalancedTemplate_DoesNotSwallowThePage()
    {
        var page = WikipediaChapterListParser.Parse(Header + "{{Graphic novel list\n| VolumeNumber = 1\n| ChapterList = {{Numbered list|start=1|A\n" + Footer);
        Assert.Empty(page.Tables);
    }

    [Fact]
    public void OversizedRange_IsBounded()
    {
        var table = Single(Header + Row("1", "| ChapterList =\n* Chapters 1-99999") + Footer);
        Assert.True(table.Volumes[0].Chapters.Count <= 2001);
    }
}
