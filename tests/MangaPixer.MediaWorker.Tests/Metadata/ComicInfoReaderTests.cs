namespace com.lifepixer.mangapixer.Tests.MediaWorker.Metadata;

using System.Text;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Archives;
using com.lifepixer.mangapixer.MediaWorker.Metadata;
using com.lifepixer.mangapixer.TestSupport;
using com.lifepixer.mangapixer.TestSupport.Fixtures;
using Xunit;

/// <summary>
/// Unit tests for the worker-side ComicInfo.xml reader (1.24.0): mapping, the
/// untrusted-input hardening (DTD/XXE/billion laughs refused, size caps, control
/// characters), entry location and archive-level outcomes.
/// </summary>
public sealed class ComicInfoReaderTests : IDisposable
{
    private readonly string _tempDir;

    public ComicInfoReaderTests()
    {
        _tempDir = TestSupport.CreateTempTestRoot("b1-comicinfo-reader");
    }

    public void Dispose() => TestSupport.CleanupDirectory(_tempDir);

    private static ComicInfoOutcome ParseString(string xml) => ComicInfoReader.Parse(Encoding.UTF8.GetBytes(xml));

    [Fact]
    public void Parse_SampleDocument_MapsEveryField()
    {
        var outcome = ParseString(ComicInfoFixtures.SampleXml());

        Assert.Equal(ComicInfoStatus.Parsed, outcome.Status);
        var p = outcome.Payload!;
        Assert.Equal("Synthetic Saga", p.Series);
        Assert.Equal("The Third Step", p.Title);
        Assert.Equal("3", p.Number);
        Assert.Equal(1, p.Volume);
        Assert.Equal(12, p.Count);
        Assert.Equal(2021, p.Year);
        Assert.Equal(4, p.Month);
        Assert.Equal("A synthetic summary.\nSecond line.", p.Summary);
        Assert.Equal("Synthetic Press", p.Publisher);
        Assert.Equal(["Action", "Fantasy"], p.Genres);
        Assert.Equal(["tag-one", "tag-two"], p.Tags);
        Assert.Equal("en", p.LanguageIso);
        Assert.Equal(2, p.MangaDirection);
        Assert.Single(p.WebUrls);
        Assert.Contains(p.Creators, c => c.Name == "Test Writer" && c.Role == "writer");
        Assert.Contains(p.Creators, c => c.Name == "Second Writer" && c.Role == "writer");
        Assert.Contains(p.Creators, c => c.Name == "Test Artist" && c.Role == "penciller");
    }

    [Fact]
    public void Parse_V1SchemaWithoutNamespaces_Parses()
    {
        var outcome = ParseString("<ComicInfo><Series>Old Tagger</Series><Number>1</Number><Year>2008</Year></ComicInfo>");

        Assert.Equal(ComicInfoStatus.Parsed, outcome.Status);
        Assert.Equal("Old Tagger", outcome.Payload!.Series);
        Assert.Equal(2008, outcome.Payload.Year);
    }

    [Fact]
    public void Parse_EmptyRoot_IsParsedWithEmptyPayload()
    {
        var outcome = ParseString("<ComicInfo/>");

        Assert.Equal(ComicInfoStatus.Parsed, outcome.Status);
        Assert.Null(outcome.Payload!.Series);
        Assert.Empty(outcome.Payload.Creators);
    }

    [Fact]
    public void Parse_Doctype_ExternalEntity_IsRefusedAsMalformed()
    {
        // XXE: an external entity pointing at a local file must never be resolved.
        var xml = "<?xml version=\"1.0\"?>\n<!DOCTYPE ComicInfo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>\n"
            + "<ComicInfo><Series>&xxe;</Series></ComicInfo>";

        var outcome = ParseString(xml);

        Assert.Equal(ComicInfoStatus.Malformed, outcome.Status);
        Assert.Null(outcome.Payload);
    }

    [Fact]
    public void Parse_BillionLaughs_IsRefusedAsMalformed()
    {
        var xml = "<?xml version=\"1.0\"?>\n<!DOCTYPE lolz [\n"
            + "<!ENTITY lol \"lol\">\n"
            + "<!ENTITY lol1 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\">\n"
            + "<!ENTITY lol2 \"&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;\">\n"
            + "<!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">\n"
            + "]>\n<ComicInfo><Series>&lol3;</Series></ComicInfo>";

        var outcome = ParseString(xml);

        Assert.Equal(ComicInfoStatus.Malformed, outcome.Status);
    }

    [Fact]
    public void Parse_Garbage_IsMalformed()
    {
        Assert.Equal(ComicInfoStatus.Malformed, ParseString("this is not xml").Status);
        Assert.Equal(ComicInfoStatus.Malformed, ParseString("<ComicInfo><Series>unclosed</ComicInfo>").Status);
    }

    [Fact]
    public void Parse_WrongRootElement_IsMalformed()
    {
        Assert.Equal(ComicInfoStatus.Malformed, ParseString("<package><Series>x</Series></package>").Status);
    }

    [Fact]
    public void Parse_OversizedBytes_IsTooLarge()
    {
        var bytes = new byte[ComicInfoLimits.MaxXmlBytes + 1];

        Assert.Equal(ComicInfoStatus.TooLarge, ComicInfoReader.Parse(bytes).Status);
    }

    [Fact]
    public void Parse_Utf8Bom_And_Utf16_AreDecoded()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"utf-16\"?><ComicInfo><Series>Café 日本</Series></ComicInfo>";
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
        var utf8Bom = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("<ComicInfo><Series>Café</Series></ComicInfo>")).ToArray();

        Assert.Equal("Café 日本", ComicInfoReader.Parse(utf16).Payload!.Series);
        Assert.Equal("Café", ComicInfoReader.Parse(utf8Bom).Payload!.Series);
    }

    [Fact]
    public void Parse_ControlAndFormatCharacters_AreStripped()
    {
        // &#x7; (bell) is legal in XML 1.0 only as a reference in some readers; use
        // allowed chars plus a bidi override (U+202E) and a zero-width no-break space.
        var outcome = ParseString("<ComicInfo><Series>  Bad‮Series﻿\tName  </Series><Title>a\nb</Title></ComicInfo>");

        Assert.Equal("BadSeries Name", outcome.Payload!.Series);
        Assert.Equal("a b", outcome.Payload.Title);
    }

    [Fact]
    public void Parse_LongFields_AreCapped()
    {
        var longSeries = new string('s', 5000);
        var longSummary = new string('m', 40_000);
        var outcome = ParseString($"<ComicInfo><Series>{longSeries}</Series><Summary>{longSummary}</Summary><Number>{new string('9', 100)}</Number></ComicInfo>");

        Assert.Equal(ComicInfoPayload.MaxShortText, outcome.Payload!.Series!.Length);
        Assert.Equal(ComicInfoPayload.MaxSummaryText, outcome.Payload.Summary!.Length);
        Assert.Equal(ComicInfoPayload.MaxCodeText, outcome.Payload.Number!.Length);
    }

    [Fact]
    public void Parse_Lists_AreDedupedAndCapped()
    {
        var genres = string.Join(",", Enumerable.Range(0, 300).Select(i => $"g{i}")) + ",g1,G1";
        var outcome = ParseString($"<ComicInfo><Genre>{genres}</Genre></ComicInfo>");

        Assert.Equal(ComicInfoPayload.MaxListItems, outcome.Payload!.Genres.Count);
        Assert.Single(outcome.Payload.Genres, g => g.Equals("g1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_Web_KeepsOnlyHttpUrls()
    {
        var outcome = ParseString("<ComicInfo><Web>https://a.example/x javascript:alert(1) ftp://b.example/y http://c.example/z not-a-url</Web></ComicInfo>");

        Assert.Equal(["https://a.example/x", "http://c.example/z"], outcome.Payload!.WebUrls);
    }

    [Theory]
    [InlineData("No", 0)]
    [InlineData("Yes", 1)]
    [InlineData("YesAndRightToLeft", 2)]
    [InlineData("Unknown", null)]
    public void Parse_Manga_MapsToDirection(string value, int? expected)
    {
        Assert.Equal(expected, ParseString($"<ComicInfo><Manga>{value}</Manga></ComicInfo>").Payload!.MangaDirection);
    }

    [Fact]
    public void Parse_OutOfRangeNumbers_AreDropped()
    {
        var p = ParseString("<ComicInfo><Year>-1</Year><Month>13</Month><Count>-1</Count><Volume>abc</Volume></ComicInfo>").Payload!;

        Assert.Null(p.Year);
        Assert.Null(p.Month);
        Assert.Null(p.Count);
        Assert.Null(p.Volume);
    }

    [Fact]
    public void Parse_UnknownAndNestedElements_AreSkipped()
    {
        var outcome = ParseString("<ComicInfo><Pages><Page Image=\"0\"/></Pages><Characters>x</Characters><Series>Kept</Series></ComicInfo>");

        Assert.Equal(ComicInfoStatus.Parsed, outcome.Status);
        Assert.Equal("Kept", outcome.Payload!.Series);
    }

    [Fact]
    public void Locate_PrefersRoot_AcceptsSingleNested_RejectsAmbiguous()
    {
        static ArchiveEntryInfo E(int i, string path) => new() { Ordinal = i, EntryPath = path, UncompressedSize = 10 };

        Assert.Equal("ComicInfo.xml", ComicInfoReader.Locate([E(0, "sub/ComicInfo.xml"), E(1, "ComicInfo.xml")])!.EntryPath);
        Assert.Equal("sub/comicinfo.XML", ComicInfoReader.Locate([E(0, "sub/comicinfo.XML"), E(1, "p.png")])!.EntryPath);
        Assert.Null(ComicInfoReader.Locate([E(0, "a/ComicInfo.xml"), E(1, "b/ComicInfo.xml")]));
        Assert.Null(ComicInfoReader.Locate([E(0, "__MACOSX/ComicInfo.xml"), E(1, "p.png")]));
    }

    [Fact]
    public async Task ReadAsync_ZipWithComicInfo_IsParsed()
    {
        var path = ComicInfoFixtures.CreateZipWithComicInfo(_tempDir, "with.cbz", ComicInfoFixtures.SampleXml());
        using var reader = await ArchiveReader.OpenAsync(path);
        var enumeration = reader.EnumerateEntries();

        var outcome = await ComicInfoReader.ReadAsync(reader, enumeration.Entries, enumeration.IsSolid, ComicInfoLimits.MaxXmlBytes, CancellationToken.None);

        Assert.Equal(ComicInfoStatus.Parsed, outcome.Status);
        Assert.Equal("Synthetic Saga", outcome.Payload!.Series);
    }

    [Fact]
    public async Task ReadAsync_ZipWithoutComicInfo_IsAbsent()
    {
        var path = ComicInfoFixtures.CreateZipWithComicInfo(_tempDir, "without.cbz", xml: null);
        using var reader = await ArchiveReader.OpenAsync(path);
        var enumeration = reader.EnumerateEntries();

        var outcome = await ComicInfoReader.ReadAsync(reader, enumeration.Entries, enumeration.IsSolid, ComicInfoLimits.MaxXmlBytes, CancellationToken.None);

        Assert.Equal(ComicInfoStatus.Absent, outcome.Status);
    }

    [Fact]
    public async Task ReadAsync_EntryOverTheCap_IsTooLarge_WithoutParsing()
    {
        var padding = new string(' ', 4096);
        var path = ComicInfoFixtures.CreateZipWithComicInfoBytes(_tempDir, "big.cbz",
            Encoding.UTF8.GetBytes("<ComicInfo><Series>x</Series>" + padding + "</ComicInfo>"));
        using var reader = await ArchiveReader.OpenAsync(path);
        var enumeration = reader.EnumerateEntries();

        var outcome = await ComicInfoReader.ReadAsync(reader, enumeration.Entries, enumeration.IsSolid, maxXmlBytes: 1024, CancellationToken.None);

        Assert.Equal(ComicInfoStatus.TooLarge, outcome.Status);
    }

    [Fact]
    public async Task ReadAsync_SolidArchive_IsSkippedWithoutDecompressing()
    {
        var path = ComicInfoFixtures.CreateZipWithComicInfo(_tempDir, "pretend-solid.cbz", ComicInfoFixtures.SampleXml());
        using var reader = await ArchiveReader.OpenAsync(path);
        var enumeration = reader.EnumerateEntries();

        var outcome = await ComicInfoReader.ReadAsync(reader, enumeration.Entries, isSolid: true, ComicInfoLimits.MaxXmlBytes, CancellationToken.None);

        Assert.Equal(ComicInfoStatus.SkippedSolid, outcome.Status);
    }

    [Fact]
    public async Task ReadEntryBoundedAsync_StopsAtTheCap()
    {
        var path = ComicInfoFixtures.CreateZipWithComicInfoBytes(_tempDir, "bounded.cbz", new byte[10_000]);
        using var reader = await ArchiveReader.OpenAsync(path);

        Assert.Null(await reader.ReadEntryBoundedAsync("ComicInfo.xml", 5_000));
        Assert.Equal(10_000, (await reader.ReadEntryBoundedAsync("ComicInfo.xml", 20_000))!.Length);
    }
}
