namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>Unit tests for the 1.32.0 step-0 <see cref="ComicsSignals"/> contract. Synthetic names only.</summary>
public sealed class ComicsSignalsTests
{
    private static ComicsSignalInput Input(DeclaredType? declared = null, string? category = null) =>
        new(declared, category, ["Some Series 001.cbz", "Some Series 002.cbz"]);

    [Theory]
    [InlineData(DeclaredType.Comic)]
    [InlineData(DeclaredType.GraphicNovel)]
    public void DeclaredComicOrGraphicNovel_IsAStrongSign_ThatRoutes(DeclaredType declared)
    {
        var signal = ComicsSignals.Of(Input(declared));
        Assert.Equal(ComicsSignalKind.DeclaredType, signal.Kinds);
        Assert.True(signal.RoutesToComics);
    }

    [Theory]
    [InlineData(DeclaredType.Manga)]
    [InlineData(DeclaredType.Manhwa)]
    [InlineData(DeclaredType.Webtoon)]
    [InlineData(null)]
    public void OtherDeclaredTypes_AndNoCategory_GiveNoSign(DeclaredType? declared)
    {
        var signal = ComicsSignals.Of(Input(declared));
        Assert.Same(ComicsSignal.None, signal);
        Assert.False(signal.RoutesToComics);
    }

    [Theory]
    [InlineData("Comics", true)]
    [InlineData("comic", true)]
    [InlineData("Manga", false)]
    [InlineData("Comics and Manga", false)]
    [InlineData("Graphic Novels", true)]
    public void ComicsCategoryFolder_IsAStrongSign(string category, bool expected)
    {
        Assert.Equal(expected, ComicsSignals.Of(Input(category: category)).RoutesToComics);
    }

    [Fact]
    public void OneWeakSign_DoesNotRoute_TwoDo()
    {
        Assert.False(new ComicsSignal(ComicsSignalKind.IssueNumbering).RoutesToComics);
        Assert.False(new ComicsSignal(ComicsSignalKind.CollectedFormatWord).RoutesToComics);
        Assert.True(new ComicsSignal(ComicsSignalKind.IssueNumbering | ComicsSignalKind.StartYearAfterName).RoutesToComics);
        Assert.True(new ComicsSignal(ComicsSignalKind.WesternPublisher).RoutesToComics);
        Assert.True(new ComicsSignal(ComicsSignalKind.ComicsIdInComicInfo | ComicsSignalKind.IssueNumbering).RoutesToComics);
    }

    [Fact]
    public void StrongAndWeakMasks_CoverEveryKind_WithoutOverlap()
    {
        var all = Enum.GetValues<ComicsSignalKind>().Aggregate(ComicsSignalKind.None, (a, k) => a | k);
        Assert.Equal(ComicsSignal.Strong | ComicsSignal.Weak, all);
        Assert.Equal(ComicsSignalKind.None, ComicsSignal.Strong & ComicsSignal.Weak);
    }
}

/// <summary>
/// The 1.32.0 lane-A detectors. Synthetic names and public, well-known series titles only; every comics shape has a manga
/// counterpart that must NOT route (the owner's library is nearly all manga).
/// </summary>
public sealed class ComicsSignalDetectorTests
{
    private static ComicsSignal Of(
        string[] names, string? folder = null, string? category = null, DeclaredType? declared = null, string? publisher = null,
        string? imprint = null, string[]? web = null, string? notes = null, int? median = null, bool saysManga = false) =>
        ComicsSignals.Of(new ComicsSignalInput(declared, category, names, publisher, web, notes, median, folder, imprint, saysManga));

    // --- issue runs (US) ---

    [Fact]
    public void UsIssueRun_WithAStartYearFolder_Routes()
    {
        var signal = Of(["Saga #001 (2012) (Digital).cbz", "Saga #002 (2012) (Digital).cbz", "Saga #003 (2012) (Digital).cbz"],
            folder: "Saga (2012)");
        Assert.Equal(ComicsSignalKind.IssueNumbering | ComicsSignalKind.StartYearAfterName, signal.Kinds);
        Assert.Equal(2012, signal.StartYear);
        Assert.True(signal.RoutesToComics);
    }

    [Theory]
    [InlineData("Saga #12.cbz")]
    [InlineData("Saga #12.1.cbz")]
    [InlineData("Saga #1/2.cbz")]
    [InlineData("Saga Issue 12.cbz")]
    [InlineData("Saga - Issue #12.cbz")]
    [InlineData("Saga No. 12.cbz")]
    [InlineData("Saga N°12.cbz")]
    [InlineData("Saga 3 (of 6).cbz")]
    [InlineData("Saga Annual 2.cbz")]
    [InlineData("Saga Annual.cbz")]
    [InlineData("FCBD 2019 - Saga.cbz")]
    public void IssueGrammar_IsRead(string name) => Assert.True(ComicsSignals.IsIssueNamed(name));

    [Theory]
    [InlineData("No. 6 v01.cbz")] // a title, not an issue number
    [InlineData("Some Title v01.cbz")]
    [InlineData("Some Title c012.cbz")]
    [InlineData("Some Title - Chapter 12.cbz")]
    [InlineData("Some Title [#12 Scans].cbz")] // inside a tag
    [InlineData("Annually Yours v01.cbz")]
    public void IssueGrammar_LeavesMangaNamesAlone(string name) => Assert.False(ComicsSignals.IsIssueNamed(name));

    [Fact]
    public void HashNumbersAlone_AreWeak_AndDoNotRoute()
    {
        // The kickoff measurement: the owner's six folders with #N files are manga.
        var signal = Of(["Some Title #01.cbz", "Some Title #02.cbz", "Some Title #03.cbz"], folder: "Some Title");
        Assert.Equal(ComicsSignalKind.IssueNumbering, signal.Kinds);
        Assert.False(signal.RoutesToComics);
    }

    [Fact]
    public void OneIssueNamedFileAmongMany_IsNoSign()
    {
        var signal = Of(["Some Title v01.cbz", "Some Title v02.cbz", "Some Title v03.cbz", "Some Title #4.cbz"]);
        Assert.Same(ComicsSignal.None, signal);
    }

    // --- collected books ---

    [Fact]
    public void TpbFolder_WithAStartYear_Routes()
    {
        var signal = Of(["Saga v01 TPB (2013).cbz", "Saga v02 TPB (2014).cbz"], folder: "Saga (2012)", median: 160);
        Assert.Equal(ComicsSignalKind.CollectedFormatWord | ComicsSignalKind.StartYearAfterName, signal.Kinds);
        Assert.Equal(ComicsPageShape.Collected, signal.PageShape);
        Assert.True(signal.RoutesToComics);
    }

    [Fact]
    public void OneShotGraphicNovel_Routes()
    {
        var signal = Of(["Some Graphic Story (2019) (OGN).cbz"], folder: "Some Graphic Story (2019)", median: 140);
        Assert.Equal(ComicsSignalKind.CollectedFormatWord | ComicsSignalKind.StartYearAfterName, signal.Kinds);
        Assert.True(signal.RoutesToComics);
    }

    [Theory]
    [InlineData("Some Title Omnibus v01.cbz", "Some Title Omnibus v02.cbz")]
    [InlineData("Some Title Vol. 3 Deluxe.cbz", "Some Title Vol. 4 Deluxe.cbz")]
    [InlineData("Some Title v01 (Complete Edition).cbz", "Some Title v02 (Complete Edition).cbz")]
    public void MangaEditionWords_AloneDoNotRoute(string a, string b)
    {
        var signal = Of([a, b], folder: "Some Title");
        Assert.False(signal.RoutesToComics);
    }

    [Theory]
    [InlineData("Saga TPB.cbz", true)]
    [InlineData("Saga HC.cbz", true)]
    [InlineData("Saga GN.cbz", true)]
    [InlineData("Saga (OGN).cbz", true)]
    [InlineData("Saga - Library Edition v1.cbz", true)]
    [InlineData("Asterix - L'Intégrale 1.cbz", true)]
    [InlineData("Some Title Gesamtausgabe 1.cbz", true)]
    [InlineData("Some Title Integraal 1.cbz", true)]
    [InlineData("Saga hc.cbz", false)] // two letters only upper-case
    [InlineData("Saga Change.cbz", false)]
    public void FormatWords_AreRead(string name, bool expected) => Assert.Equal(expected, ComicsSignals.HasCollectedFormatWord(name));

    // --- BD / European albums ---

    [Fact]
    public void FrenchAlbums_UnderABdFolder_Route_WithTheFrenchOrigin()
    {
        var signal = Of(["Asterix T01 - Asterix le Gaulois.cbz", "Asterix T02 - La Serpe d'or.cbz"], folder: "Asterix", category: "bd");
        Assert.Equal(ComicsSignalKind.CategoryFolder | ComicsSignalKind.AlbumNumbering, signal.Kinds);
        Assert.True(signal.RoutesToComics);
        Assert.Equal([MetadataOrigin.French], AutoMatchText.OriginsForCategory("bd")!);
    }

    [Theory]
    [InlineData("Blake et Mortimer - Tome 3.cbz")]
    [InlineData("Suske en Wiske Deel 12.cbz")]
    [InlineData("Mortadelo y Filemon Tomo 3.cbz")]
    [InlineData("Some Series Band 4.cbz")]
    [InlineData("Some Series Album 4.cbz")]
    [InlineData("Some Series Livre 2.cbz")]
    [InlineData("Bone Book One.cbz")]
    public void AlbumGrammar_IsRead(string name) => Assert.True(ComicsSignals.IsAlbumNamed(name));

    [Theory]
    [InlineData("Some Title Book 1.cbz")] // Book N is also manga's
    [InlineData("Some Title v01.cbz")]
    [InlineData("Some Title [T2].cbz")] // inside a tag
    [InlineData("Brass Band Story v01.cbz")]
    public void AlbumGrammar_LeavesMangaNamesAlone(string name) => Assert.False(ComicsSignals.IsAlbumNamed(name));

    [Fact]
    public void GermanMangaBands_AloneDoNotRoute()
    {
        var signal = Of(["Some Title Band 01.cbz", "Some Title Band 02.cbz"], folder: "Some Title");
        Assert.Equal(ComicsSignalKind.AlbumNumbering, signal.Kinds);
        Assert.False(signal.RoutesToComics);
    }

    // --- start year ---

    [Theory]
    [InlineData("Saga (2012)", new string[0], 2012)]
    [InlineData("Saga (2012) (Digital)", new string[0], 2012)]
    [InlineData(null, new[] { "Saga (2012) 001.cbz", "Saga (2012) 002.cbz" }, 2012)]
    [InlineData(null, new[] { "Saga 001 (2012).cbz", "Saga 002 (2012).cbz" }, null)] // a cover date after the issue
    [InlineData("Some Title v01 (2019)", new string[0], null)]
    [InlineData("[Group] Some Title (2019)", new string[0], null)]
    [InlineData("Some Title", new string[0], null)]
    public void StartYear_IsTheYearRightAfterTheTitle(string? folder, string[] names, int? expected) =>
        Assert.Equal(expected, ComicsSignals.StartYearOf(folder, names));

    // --- manga stays manga ---

    [Theory]
    [InlineData("manga")]
    [InlineData("manhwa")]
    [InlineData("manhua")]
    [InlineData("webtoons")]
    [InlineData("doujinshi")]
    public void MangaCategoryWords_SilenceTheWeakSigns(string category)
    {
        var signal = Of(["Some Title #01.cbz", "Some Title #02.cbz"], folder: "Some Title (2019)", category: category);
        Assert.Same(ComicsSignal.None, signal);
    }

    [Theory]
    [InlineData("漫画")]
    [InlineData("만화")]
    [InlineData("漫畫")]
    [InlineData("Manga")]
    public void JapaneseKoreanChineseWords_AreNotComicsCategories(string category) =>
        Assert.False(ComicsSignals.IsComicsCategory(category));

    [Fact]
    public void ComicInfoMangaFlag_SilencesThePublisherAndTheWeakSigns()
    {
        var signal = Of(["Some Title #01.cbz", "Some Title #02.cbz"], folder: "Some Title (2019)", publisher: "Marvel", saysManga: true);
        Assert.Same(ComicsSignal.None, signal);
    }

    [Fact]
    public void DeclaredManga_SilencesEverySign_EvenAComicsId()
    {
        var signal = Of(["Saga #001.cbz"], folder: "Saga (2012)", declared: DeclaredType.Manga, publisher: "Image",
            web: ["https://comicvine.gamespot.com/saga/4050-48466/"]);
        Assert.Same(ComicsSignal.None, signal);
    }

    [Fact]
    public void PlainMangaVolumes_GiveNoSign()
    {
        Assert.Same(ComicsSignal.None, Of(["Some Title v01.cbz", "Some Title v02.cbz"], folder: "Some Title", median: 190));
        Assert.Same(ComicsSignal.None, Of(["Some Title c001.cbz", "Some Title c002.cbz"], folder: "Some Title", median: 20));
    }

    // --- publishers ---

    [Theory]
    [InlineData("Marvel", null, true)]
    [InlineData("DC Comics", null, true)]
    [InlineData("Image Comics", null, true)]
    [InlineData("BOOM! Studios", null, true)]
    [InlineData("Glénat BD", null, true)]
    [InlineData("Panini Comics", null, true)]
    [InlineData("Panini Comics", "Planet Manga", false)]
    [InlineData("Casterman", "Sakka", false)]
    [InlineData("Glénat", null, false)] // Glenat Manga
    [InlineData("Dark Horse", null, false)] // a large manga line
    [InlineData("Panini Manga", null, false)]
    [InlineData("Viz Media", null, false)]
    [InlineData(null, null, false)]
    public void WesternPublishers_AreAShortExplicitList(string? publisher, string? imprint, bool expected) =>
        Assert.Equal(expected, ComicsSignals.IsWesternPublisher(publisher, imprint));

    [Fact]
    public void WesternPublisher_IsAStrongSign()
    {
        var signal = Of(["Some Series 001.cbz"], publisher: "Image");
        Assert.Equal(ComicsSignalKind.WesternPublisher, signal.Kinds);
        Assert.True(signal.RoutesToComics);
    }

    // --- comics ids ---

    [Theory]
    [InlineData("https://comicvine.gamespot.com/saga/4050-48466/", ComicsIdSite.ComicVine, ComicsIdKind.Series, "48466")]
    [InlineData("http://comicvine.gamespot.com/saga-1/4000-338526/", ComicsIdSite.ComicVine, ComicsIdKind.Issue, "338526")]
    [InlineData("https://metron.cloud/series/saga-2012/", ComicsIdSite.Metron, ComicsIdKind.Series, "saga-2012")]
    [InlineData("https://www.comics.org/issue/123456/", ComicsIdSite.Gcd, ComicsIdKind.Issue, "123456")]
    [InlineData("https://comics.org/series/7890", ComicsIdSite.Gcd, ComicsIdKind.Series, "7890")]
    public void ComicsUrls_AreParsed(string url, ComicsIdSite site, ComicsIdKind kind, string id) =>
        Assert.Equal(new ComicsId(site, kind, id), ComicsSignals.ParseUrl(url));

    [Theory]
    [InlineData("https://www.mangaupdates.com/series/abc123/some-title")]
    [InlineData("https://comicvine.gamespot.com/")]
    [InlineData("https://evil.example/comics.org/series/1/")]
    [InlineData("not a url")]
    public void OtherUrls_AreNotComicsIds(string url) => Assert.Null(ComicsSignals.ParseUrl(url));

    [Theory]
    [InlineData("Scraped metadata from ComicVine [CVDB338526] on 2020.01.01.", ComicsIdSite.ComicVine, "338526")]
    [InlineData("Tagged with ComicTagger 1.5.5 using info from Comic Vine on 2023-01-01 12:00:00. [Issue ID 338526]", ComicsIdSite.ComicVine, "338526")]
    [InlineData("Tagged with MetronTagger [issue_id:4242]", ComicsIdSite.Metron, "4242")]
    [InlineData("urn:comicvine:issue:338526", ComicsIdSite.ComicVine, "338526")]
    public void NoteIds_AreParsed(string notes, ComicsIdSite site, string id)
    {
        var ids = ComicsSignals.IdsOf(null, [notes]);
        Assert.Contains(ids, i => i.Site == site && i.Id == id);
    }

    [Fact]
    public void CvdbSkip_IsNotAnId() => Assert.Empty(ComicsSignals.IdsOf(null, ["CVDBSKIP"]));

    [Fact]
    public void AFolderTag_NamesAnId_AndRoutes()
    {
        var signal = Of(["Some Series 001.cbz"], folder: "Some Series [gcd-77]");
        Assert.Equal(ComicsSignalKind.ComicsIdInComicInfo, signal.Kinds);
        Assert.Equal([new ComicsId(ComicsIdSite.Gcd, ComicsIdKind.Unknown, "77")], signal.Ids);
        Assert.True(signal.RoutesToComics);
    }

    [Fact]
    public void Ids_AreDeduplicated_AndCapped()
    {
        var urls = Enumerable.Range(1, 10).Select(i => $"https://www.comics.org/issue/{i}/").Prepend("https://www.comics.org/issue/1/");
        var ids = ComicsSignals.IdsOf(urls, null);
        Assert.Equal(ComicsSignals.MaxIds, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // --- category words ---

    [Theory]
    [InlineData("Comic Books")]
    [InlineData("Graphic Novels")]
    [InlineData("graphic novel")]
    [InlineData("BD")]
    [InlineData("Bandes Dessinées")]
    [InlineData("bande dessinee")]
    [InlineData("Fumetti")]
    [InlineData("Tebeos")]
    [InlineData("Historietas")]
    [InlineData("Stripboeken")]
    [InlineData("US Comics")]
    [InlineData("European Comics")]
    [InlineData("Eurocomics")]
    public void ComicsCategoryWords_AreCategoryFolders_AndStrongSigns(string word)
    {
        Assert.True(AutoMatchText.IsCategoryFolderName(word));
        Assert.True(ComicsSignals.IsComicsCategory(word.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("Strips")]
    [InlineData("Albums")]
    [InlineData("Webcomics")]
    [InlineData("Comics and Manga")]
    public void AmbiguousWords_AreNotCategories(string word)
    {
        Assert.False(AutoMatchText.IsCategoryFolderName(word));
        Assert.False(ComicsSignals.IsComicsCategory(word));
    }

    [Theory]
    [InlineData("bd", MetadataOrigin.French)]
    [InlineData("bandes dessinées", MetadataOrigin.French)]
    [InlineData("tebeos", MetadataOrigin.Spanish)]
    [InlineData("historietas", MetadataOrigin.Spanish)]
    [InlineData("fumetti", MetadataOrigin.Italian)]
    [InlineData("stripboeken", MetadataOrigin.Dutch)]
    [InlineData("us comics", MetadataOrigin.EnglishOriginal)]
    public void LanguageCategoryWords_NameAnOrigin(string word, MetadataOrigin origin) =>
        Assert.Equal([origin], AutoMatchText.OriginsForCategory(word)!);

    [Theory]
    [InlineData("comics")]
    [InlineData("graphic novels")]
    [InlineData("european comics")]
    public void GenericComicsWords_NameNoOrigin(string word) => Assert.Null(AutoMatchText.OriginsForCategory(word));

    [Fact]
    public void ComicsCategoryWords_AreNeverCreators()
    {
        Assert.False(AutoMatchText.IsAuthorLike("Graphic Novels", requireTwoTokens: true));
        Assert.False(AutoMatchText.IsAuthorLike("Comic Books", requireTwoTokens: true));
    }

    // --- page shape ---

    [Theory]
    [InlineData(null, ComicsPageShape.Unknown)]
    [InlineData(0, ComicsPageShape.Unknown)]
    [InlineData(22, ComicsPageShape.Issues)]
    [InlineData(48, ComicsPageShape.Issues)]
    [InlineData(70, ComicsPageShape.Unknown)]
    [InlineData(100, ComicsPageShape.Collected)]
    [InlineData(196, ComicsPageShape.Collected)]
    public void PageShape_FollowsTheMedian(int? median, ComicsPageShape expected) =>
        Assert.Equal(expected, ComicsSignals.PageShapeOf(median));

    [Fact]
    public void Median_IgnoresUnknownCounts()
    {
        Assert.Equal(24, ComicsSignals.MedianOf([24, null, 22, 30, 0]));
        Assert.Null(ComicsSignals.MedianOf([null, 0]));
    }

    [Fact]
    public void PageShapeAlone_NeverRoutes() =>
        Assert.Same(ComicsSignal.None, Of(["Some Title 001.cbz", "Some Title 002.cbz"], median: 22));
}

/// <summary>The 1.32.0 comics unit grammar in the shared name helpers (volumes, chapters, extras, title cleaning). Synthetic names.</summary>
public sealed class ComicsUnitGrammarTests
{
    private static decimal? D(string s) => s.Length == 0 ? null : decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    // volume, volume end, chapter, chapter end ("" = null), extra
    [Theory]
    [InlineData("Asterix T01 - Asterix le Gaulois.cbz", "1", "", "", "", false)]
    [InlineData("Blake et Mortimer - Tome 03.cbz", "3", "", "", "", false)]
    [InlineData("Some Title Tomo 3.cbz", "3", "", "", "", false)]
    [InlineData("Some Title Band 01.cbz", "1", "", "", "", false)]
    [InlineData("Suske en Wiske Deel 12.cbz", "12", "", "", "", false)]
    [InlineData("Some Series Album 4.cbz", "4", "", "", "", false)]
    [InlineData("Some Series Livre 2.cbz", "2", "", "", "", false)]
    [InlineData("Some Series Tome 1-3.cbz", "1", "3", "", "", false)]
    [InlineData("Some Series T01-T03.cbz", "1", "3", "", "", false)]
    [InlineData("Some Title [T2].cbz", "", "", "", "", false)] // a single letter in a tag is not read
    [InlineData("Some Title T2019.cbz", "", "", "", "", false)]
    [InlineData("Saga Issue 12.cbz", "", "", "12", "", false)]
    [InlineData("Saga - Issue #12.cbz", "", "", "12", "", false)]
    [InlineData("Saga No. 12.cbz", "", "", "12", "", false)]
    [InlineData("Saga N°12.cbz", "", "", "12", "", false)]
    [InlineData("No. 6 v01.cbz", "1", "", "", "", false)] // a title, not an issue
    [InlineData("Casino. 12.cbz", "", "", "", "", false)]
    [InlineData("Saga #012.cbz", "", "", "12", "", false)] // unchanged: # reads as a chapter
    [InlineData("Saga Annual #2.cbz", "", "", "2", "", true)]
    [InlineData("Saga Annual 2.cbz", "", "", "2", "", true)]
    [InlineData("Saga Special #1.cbz", "", "", "1", "", true)]
    [InlineData("Saga One-Shot 1.cbz", "", "", "1", "", true)]
    [InlineData("FCBD 2019 - Saga.cbz", "", "", "", "", false)] // a year, not a unit
    [InlineData("Some Title Special Edition v01.cbz", "1", "", "", "", false)]
    [InlineData("Some Title - Special.cbz", "", "", "", "", false)]
    [InlineData("Some Title v01 c003.cbz", "1", "", "3", "", false)] // manga unchanged
    public void UnitsOf_ReadsTheComicsGrammar(string name, string volume, string volumeEnd, string chapter, string chapterEnd, bool extra) =>
        Assert.Equal(new UnitNumbers(D(volume), D(volumeEnd), D(chapter), D(chapterEnd), extra), AutoMatchText.UnitsOf(name));

    [Theory]
    [InlineData("Asterix T05.cbz", 5, null)]
    [InlineData("Some Title Band 07.cbz", 7, null)]
    [InlineData("Saga Issue 12.cbz", null, 12)]
    [InlineData("Saga No. 12.cbz", null, 12)]
    [InlineData("No. 6 v01.cbz", 1, null)]
    public void MatcherIntegers_ReadTheComicsGrammar(string name, int? volume, int? chapter)
    {
        Assert.Equal(volume, AutoMatchText.VolumeNumberOf(name));
        Assert.Equal(chapter, AutoMatchText.ChapterNumberOf(name));
    }

    [Theory]
    [InlineData("Asterix Tome 01 - Asterix le Gaulois.cbz", "Asterix")]
    [InlineData("Largo Winch T03.cbz", "Largo Winch")]
    [InlineData("Saga Issue 12.cbz", "Saga")]
    public void ArchiveBaseTitle_CutsAtTheComicsTokens(string name, string expected) =>
        Assert.Equal(expected, TitleNormalizer.ArchiveBaseTitle(name));

    [Theory]
    [InlineData("Saga Vol. 1 TPB", "Saga", "TPB")]
    [InlineData("Saga HC", "Saga", "HC")]
    [InlineData("Saga OGN", "Saga", "OGN")]
    [InlineData("Asterix - L'Intégrale", "Asterix", "L'Intégrale")]
    [InlineData("Some Series Gesamtausgabe", "Some Series", "Gesamtausgabe")]
    [InlineData("Saga Library Edition", "Saga", "Library Edition")]
    public void FormatWords_AreRemovedFromTheTitle_AndKeptAsHints(string name, string primary, string hint)
    {
        var n = TitleNormalizer.Normalize(name);
        Assert.Equal(primary, n.Primary);
        Assert.Contains(hint, n.EditionHints);
    }

    [Theory]
    [InlineData("Absolute Boyfriend")]
    [InlineData("Compendium of Souls")]
    [InlineData("Ghost Hunt")]
    [InlineData("Brass Band Story")]
    public void TitleWords_ThatLookLikeFormats_StayInTheTitle(string name) =>
        Assert.Equal(name, TitleNormalizer.Normalize(name).Primary);

    [Theory]
    [InlineData("c2c")]
    [InlineData("Zone-Empire")]
    [InlineData("Minutemen-Empire")]
    [InlineData("TPB")]
    [InlineData("Webrip")]
    public void ComicsScanTags_AreReleaseTags(string tag) => Assert.True(ArchiveNameAnatomy.IsReleaseTag(tag));

    [Fact]
    public void ComicsScanTags_AreNeverCreators() =>
        Assert.Empty(AutoMatchText.CreatorHints("Saga #001 (2012) (Digital) (Zone-Empire).cbz"));

    [Theory]
    [InlineData("https://metron.cloud/series/saga-2012/", true)]
    [InlineData("https://www.comics.org/series/12345/", true)]
    [InlineData("https://comics.org/issue/1/", true)]
    [InlineData("https://comicvine.gamespot.com/saga/4050-48466/", true)]
    [InlineData("https://comics.example/issue/1/", false)]
    public void ComicsDatabaseLinks_AreShown(string url, bool expected) => Assert.Equal(expected, MetadataWebLinks.IsAllowed(url));
}
