namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using System.Text;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using Xunit;

/// <summary>Recorded MangaDex responses (embedded resources; see Fixtures/MangaDex/README.md).</summary>
public static class MdFixtures
{
    public const string BerserkId = "801513ba-a712-498c-8f57-cae55b38cc92";
    public const string ChainsawManId = "a77742b1-befd-49a4-bff5-1ad4e6b0ef7b";
    public const string TowerOfGodId = "c0ee660b-f9f2-45c3-8068-5123ff53f84a";
    public const string JojoPart3Id = "0d545e62-d4cd-4e65-a65c-a5c46b794918";

    // MangaUpdates ids of the same series (golden set).
    public const string MuBerserk = "51239621230";
    public const string MuChainsawMan = "75336092483";
    public const string MuTowerOfGod = "13015731700";
    public const string MuJojoPart3 = "60420553585";
    public const string MuJigokurakuKaku = "61508275290";
    public const string MuJigokuraku2005 = "10294535868";

    public static string Load(string name)
    {
        using var stream = typeof(MdFixtures).Assembly.GetManifestResourceStream($"MangaDex.{name}.json")
            ?? throw new InvalidOperationException("Missing fixture " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<MangaDexManga> Search(string name)
    {
        using var doc = JsonDocument.Parse(Load("search." + name));
        return doc.RootElement.GetProperty("data").EnumerateArray().Select(MangaDexProvider.ReadManga).OfType<MangaDexManga>().ToList();
    }

    public static IReadOnlyList<MangaDexAggregateVolume> Aggregate(string name)
    {
        using var doc = JsonDocument.Parse(Load("aggregate." + name));
        return MangaDexProvider.ReadAggregate(doc.RootElement);
    }

    public static MangaDexCoverPage Covers(string name)
    {
        using var doc = JsonDocument.Parse(Load("cover." + name));
        return MangaDexProvider.ReadCovers(doc.RootElement);
    }
}

/// <summary>
/// Unit tests (1.29.0) of the MangaDex companion's pure parts on RECORDED responses: the JSON readers (the <c>[]</c>
/// shapes, links, tags, the cover relation), the cross-link rule of design 1.3 (links.mu decode, parenthetical strip,
/// query 2, the tie-breaks, the rejected namesake), the reference parser, the image address and the volume list built
/// from <c>aggregate</c>. No network: nothing here sends a request.
/// </summary>
public sealed class MangaDexProviderTests
{
    [Fact]
    public void ReadManga_TakesTitlesLinksLanguageAndTheCoverFile()
    {
        var berserk = MdFixtures.Search("berserk")[0];
        Assert.Equal(MdFixtures.BerserkId, berserk.Id);
        Assert.Equal("Berserk", berserk.Title);
        Assert.Equal("njeqwry", berserk.MangaUpdatesLink);
        Assert.Equal("30002", berserk.AniListId);
        Assert.Equal("ja", berserk.OriginalLanguage);
        Assert.NotNull(berserk.MainCoverFile);
        Assert.True(MangaDexProvider.IsValidFileName(berserk.MainCoverFile));
        Assert.False(berserk.FanColored);
        Assert.NotNull(berserk.CreatedAt);
    }

    [Fact]
    public void ReadManga_ReadsTheFanColoredTag_AndToleratesMissingLinks()
    {
        var results = MdFixtures.Search("chainsaw-man");
        Assert.Contains(results, m => m.FanColored);
        Assert.Contains(results, m => m.MangaUpdatesLink is null && m.AniListId is null);

        using var doc = JsonDocument.Parse("""{"id":"00000000-0000-4000-8000-000000000001","attributes":{"title":{"ja-ro":"Synthetic"},"links":[],"altTitles":[],"tags":[]},"relationships":[]}""");
        var manga = MangaDexProvider.ReadManga(doc.RootElement)!;
        Assert.Equal("Synthetic", manga.Title);
        Assert.Null(manga.MangaUpdatesLink);
        Assert.Null(manga.MainCoverFile);

        using var bad = JsonDocument.Parse("""{"id":"../../etc","attributes":{"title":{"en":"x"}}}""");
        Assert.Null(MangaDexProvider.ReadManga(bad.RootElement));
    }

    [Fact]
    public void ReadAggregate_AcceptsAnEmptyList_AndReadsTheNoneBucket()
    {
        Assert.Empty(MdFixtures.Aggregate("tower-of-god")); // "volumes": []
        var berserk = MdFixtures.Aggregate("berserk");
        Assert.Contains(berserk, v => v.Volume == "none");
        Assert.Contains(berserk, v => v.Volume == "1" && v.Chapters.Count > 0);

        using var chaptersAsList = JsonDocument.Parse("""{"result":"ok","volumes":{"1":{"volume":"1","count":0,"chapters":[]}}}""");
        Assert.Empty(MangaDexProvider.ReadAggregate(chaptersAsList.RootElement).Single().Chapters);
    }

    [Fact]
    public void ReadCovers_TakesVolumeLocaleFileAndTheMangaRelation()
    {
        var page = MdFixtures.Covers("berserk");
        Assert.Equal(99, page.Total);
        Assert.All(page.Covers, c => Assert.Equal(MdFixtures.BerserkId, c.MangaId));
        Assert.All(page.Covers, c => Assert.Contains(c.Locale, new[] { "en", "ja" }));
        Assert.Contains(page.Covers, c => c.Volume == "1.1"); // edition alternates are listed with their own key
        Assert.Contains(page.Covers, c => c.Volume == "1" && c.Locale == "ja");
    }

    [Theory]
    [InlineData("njeqwry", MdFixtures.MuBerserk, true)]
    [InlineData("NJEQWRY", MdFixtures.MuBerserk, true)]
    [InlineData("51239621230", MdFixtures.MuBerserk, true)] // a long all-digit value that equals the decimal id
    [InlineData("12345", "12345", false)] // a short all-digit value is a legacy id: never a match
    [InlineData("ylx5wzn", MdFixtures.MuBerserk, false)]
    [InlineData(null, MdFixtures.MuBerserk, false)]
    [InlineData("njeqwry", "not-a-number", false)]
    public void LinksTo_DecodesBase36_AndRefusesLegacyIds(string? link, string muId, bool expected) =>
        Assert.Equal(expected, MangaDexCrossLink.LinksTo(link, muId));

    [Theory]
    [InlineData("Jigokuraku (KAKU Yuuji)", "Jigokuraku")]
    [InlineData("Look Back (FUJIMOTO Tatsuki)", "Look Back")]
    [InlineData("Berserk", "Berserk")]
    [InlineData("(Untitled)", "(Untitled)")]
    [InlineData("  Chainsaw   Man ", "Chainsaw Man")]
    public void Query1_StripsATrailingParenthetical(string title, string expected) =>
        Assert.Equal(expected, MangaDexCrossLink.Query1(title));

    [Fact]
    public void Query2_IsTheFirstAsciiMultiWordAssociatedTitle_ThatDiffersFromQuery1()
    {
        Assert.Equal("Hell's Paradise Jigokuraku",
            MangaDexCrossLink.Query2(["地獄楽", "Jigokuraku", "JIGOKURAKU!", "Hells", "Hell's Paradise Jigokuraku", "Another Title"], "Jigokuraku"));
        Assert.Null(MangaDexCrossLink.Query2(["地獄楽", "Single"], "Jigokuraku"));
    }

    [Fact]
    public void Pick_AcceptsOnlyARecordWhoseOwnLinkNamesTheSeries()
    {
        Assert.Equal(MdFixtures.BerserkId, MangaDexCrossLink.Pick(MdFixtures.Search("berserk"), MdFixtures.MuBerserk)!.Id);
        Assert.Equal("cb77e4a6", MangaDexCrossLink.Pick(MdFixtures.Search("jigokuraku"), MdFixtures.MuJigokurakuKaku)!.Id[..8]);
        // The 2005 one-shot of the same name: its namesake links to a DIFFERENT MangaUpdates record - rejected.
        Assert.Null(MangaDexCrossLink.Pick(MdFixtures.Search("jigokuraku"), MdFixtures.MuJigokuraku2005));
    }

    [Fact]
    public void Pick_TieBreaks_PreferTheRecordWithAnAniListLink_ThenNotFanColored_ThenTheOldest()
    {
        // The official colour edition shares the link but has no AniList link.
        Assert.Equal(MdFixtures.ChainsawManId, MangaDexCrossLink.Pick(MdFixtures.Search("chainsaw-man"), MdFixtures.MuChainsawMan)!.Id);
        // A "Book Version" ranked first without an AniList link loses to the record that has one.
        Assert.Equal(MdFixtures.TowerOfGodId, MangaDexCrossLink.Pick(MdFixtures.Search("tower-of-god"), MdFixtures.MuTowerOfGod)!.Id);
        // Two JoJo records with the same link: the one with the AniList link.
        Assert.Equal(MdFixtures.JojoPart3Id, MangaDexCrossLink.Pick(MdFixtures.Search("jojo-part-3"), MdFixtures.MuJojoPart3)!.Id);

        // A stored AniList id wins over any other AniList link; then not fan-coloured; then the oldest record.
        MangaDexManga M(string id, string? al, bool fan, int year) => new()
        {
            Id = id, Title = "T", MangaUpdatesLink = "njeqwry", AniListId = al, FanColored = fan,
            CreatedAt = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var a = M("00000000-0000-4000-8000-00000000000a", "1", false, 2020);
        var b = M("00000000-0000-4000-8000-00000000000b", "2", false, 2021);
        Assert.Equal(b.Id, MangaDexCrossLink.Pick([a, b], MdFixtures.MuBerserk, storedAniListId: "2")!.Id);
        var fan = M("00000000-0000-4000-8000-00000000000c", "1", true, 2010);
        Assert.Equal(a.Id, MangaDexCrossLink.Pick([fan, a], MdFixtures.MuBerserk)!.Id);
        var older = M("00000000-0000-4000-8000-00000000000d", "3", false, 2019);
        Assert.Equal(older.Id, MangaDexCrossLink.Pick([a, older], MdFixtures.MuBerserk)!.Id);
    }

    [Theory]
    [InlineData("801513ba-a712-498c-8f57-cae55b38cc92", true)]
    [InlineData("https://mangadex.org/title/801513ba-a712-498c-8f57-cae55b38cc92/berserk", true)]
    [InlineData("mangadex.org/title/801513BA-A712-498C-8F57-CAE55B38CC92", true)]
    [InlineData("https://evil.example/title/801513ba-a712-498c-8f57-cae55b38cc92", false)]
    [InlineData("https://mangadex.org/chapter/801513ba-a712-498c-8f57-cae55b38cc92", false)]
    [InlineData("berserk", false)]
    [InlineData("", false)]
    public void TryParseReference_AcceptsAUuidOrATitleUrl_Only(string input, bool ok)
    {
        Assert.Equal(ok, MangaDexProvider.TryParseReference(input, out var id));
        if (ok)
            Assert.Equal(MdFixtures.BerserkId, id);
    }

    [Fact]
    public void CoverImageUrl_IsRebuiltFromIdAndFileName_On512Pixels_OrNullWhenInvalid()
    {
        Assert.Equal("https://uploads.mangadex.org/covers/" + MdFixtures.BerserkId + "/abc-1.jpg.512.jpg",
            MangaDexProvider.CoverImageUrl(MdFixtures.BerserkId, "abc-1.jpg"));
        Assert.Null(MangaDexProvider.CoverImageUrl(MdFixtures.BerserkId, "../x.jpg"));
        Assert.Null(MangaDexProvider.CoverImageUrl("not-a-uuid", "abc.jpg"));
        Assert.Null(MangaDexProvider.CoverImageUrl(MdFixtures.BerserkId, "abc.svg"));
    }

    [Fact]
    public void SearchUrl_SendsTheTitleAndTheFixedParametersOnly()
    {
        var url = new Uri(MangaDexProvider.SearchUrl("Chainsaw Man"));
        Assert.Equal(MetadataHttp.MangaDexApiHost, url.Host);
        Assert.Equal("/manga", url.AbsolutePath);
        var query = Uri.UnescapeDataString(url.Query);
        Assert.Equal("?title=Chainsaw Man&limit=10&order[relevance]=desc&contentRating[]=safe&contentRating[]=suggestive"
            + "&contentRating[]=erotica&contentRating[]=pornographic&includes[]=cover_art", query);
    }

    [Fact]
    public void VolumeList_FromRecordedAggregates()
    {
        var berserk = VolumeListBuilder.Build(Raw(MdFixtures.Aggregate("berserk")));
        Assert.True(berserk.Volumes.Count >= 40);
        Assert.Equal("1", berserk.Volumes[0].Volume);
        Assert.True(berserk.ChaptersPerVolume > 5);
        var ordered = berserk.Volumes.Select(v => VolumeMapJson.Parse(v.Volume)!.Value).ToList();
        Assert.Equal(ordered.Order(), ordered);

        // JoJo Part 3's aggregate numbers volumes continuously across parts (28 here); its own covers stop at 16.
        Assert.Equal(28, VolumeListBuilder.Build(Raw(MdFixtures.Aggregate("jojo-part-3"))).HighestVolume);
        Assert.Equal(16, MdFixtures.Covers("jojo-part-3").Covers.Select(c => VolumeMapJson.Parse(c.Volume)).Max());

        Assert.Empty(VolumeListBuilder.Build(Raw(MdFixtures.Aggregate("tower-of-god"))).Volumes);
    }

    private static IReadOnlyList<VolumeListBuilder.RawVolume> Raw(IReadOnlyList<MangaDexAggregateVolume> volumes) =>
        volumes.Select(v => new VolumeListBuilder.RawVolume(v.Volume, v.Chapters.Select(c => (c.Chapter, c.Count)).ToList())).ToList();
}
