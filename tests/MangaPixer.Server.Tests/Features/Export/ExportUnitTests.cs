namespace com.lifepixer.mangapixer.Tests.Server.Features.Export;

using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes.Wikipedia;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Xunit;

/// <summary>Unit tests of the metadata export's pure parts (1.33.0): official links, volume projection, cursor, include, JSON, vocabulary.</summary>
[Trait("Category", "Unit")]
public sealed class ExportUnitTests
{
    private static readonly DateOnly Today = new(2026, 10, 3);

    [Fact]
    public void OfficialLinks_AreReadFromTheRecordedMangaDexAnswer_InTheFixedKeyOrder()
    {
        using var doc = JsonDocument.Parse(MdFixtures.Load("search.berserk"));
        var berserk = doc.RootElement.GetProperty("data").EnumerateArray().Select(MangaDexProvider.ReadManga).OfType<MangaDexManga>()
            .Single(m => m.Id == MdFixtures.BerserkId);

        Assert.Equal(["raw", "engtl", "bw", "amz", "ebj", "cdj"], berserk.OfficialLinks.Select(l => l.Key));
        Assert.Equal("https://bookwalker.jp/series/16664/list", berserk.OfficialLinks.Single(l => l.Key == "bw").Url);
        Assert.All(berserk.OfficialLinks, l => Assert.StartsWith("https://", l.Url, StringComparison.Ordinal));
        // The cross-links MangaPixer already read are unchanged.
        Assert.Equal(("njeqwry", "30002"), (berserk.MangaUpdatesLink, berserk.AniListId));
    }

    [Fact]
    public void OfficialLinks_AreEmpty_WhenTheRecordHasNone()
    {
        using var doc = JsonDocument.Parse(MdFixtures.Load("search.chainsaw-man"));
        var bare = doc.RootElement.GetProperty("data").EnumerateArray().Select(MangaDexProvider.ReadManga).OfType<MangaDexManga>()
            .Single(m => m.Id == "7ce2efff-b549-4726-beb8-b8423c5bd55c");
        Assert.Empty(bare.OfficialLinks);
    }

    [Theory]
    [InlineData("raw", "javascript:alert(1)")]
    [InlineData("raw", "ftp://files.example.com/x")]
    [InlineData("raw", "https://user:secret@example.com/x")]
    [InlineData("raw", "//example.com/x")]
    [InlineData("raw", "not a url")]
    [InlineData("raw", "https://localhost/x")]
    [InlineData("raw", "https://127.0.0.1/x")]
    [InlineData("amz", "")]
    [InlineData("mal", "https://myanimelist.net/manga/2")]
    [InlineData("bw", "../../etc")]
    [InlineData("bw", "https://evil.example.com/x")]
    [InlineData("bw", "series//x")]
    [InlineData("bw", "/series/1")]
    public void OfficialLinkUrl_RefusesWhatIsNotASafeHttpUrl(string key, string value)
    {
        Assert.Null(MangaDexProvider.OfficialLinkUrl(key, value));
    }

    [Fact]
    public void OfficialLinkUrl_RefusesAnOverlongValue()
    {
        Assert.Null(MangaDexProvider.OfficialLinkUrl("raw", "https://example.com/" + new string('a', 600)));
    }

    [Fact]
    public void StoredOfficialLinks_MapToKindsAndLabels_AndUnknownKeysAreDropped()
    {
        var json = JsonSerializer.Serialize(new CompanionLinkService.MangaDexExtra("ja", null, null)
        {
            Links = [new("raw", "https://publisher.example.com/s/1"), new("zz", "https://x.example.com/"), new("amz", "https://store.example.com/dp/1")],
        });
        var links = ExportItemBuilder.OfficialLinks(json);
        Assert.Equal([("publisher", "Official (original language)", "mangadex"), ("store", "Amazon", "mangadex")],
            links.Select(l => (l.Kind, l.Label, l.Source)));
        // A row written before 1.33.0 has no Links key.
        Assert.Empty(ExportItemBuilder.OfficialLinks("{\"OriginalLanguage\":\"ja\",\"MainCoverId\":null,\"MainCoverFile\":null}"));
        Assert.Empty(ExportItemBuilder.OfficialLinks(null));
    }

    private static SeriesVolumeMapEntity Map(VolumeMapSource source, string volumesJson, DateTimeOffset fetched) => new()
    {
        Source = (int)source,
        State = (int)VolumeMapState.Ok,
        VolumesJson = volumesJson,
        ContentHash = "h",
        FetchedAt = fetched,
    };

    [Fact]
    public void Volumes_MergeMangaDexWithWikipedia_AndCarryDatesAndIsbns()
    {
        var mdAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var wpAt = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        var maps = new[]
        {
            Map(VolumeMapSource.MangaDexAggregate, "[{\"v\":\"1\",\"c\":[\"1\",\"2\",\"3\"]},{\"v\":\"2\",\"c\":[\"4\",\"5\",\"6\"]}]", mdAt),
            Map(VolumeMapSource.WikipediaList, "[{\"v\":\"2\",\"c\":[\"4\",\"5\",\"6\",\"7\"]},{\"v\":\"3\",\"c\":[\"8\",\"9\",\"10.5\"]}]", wpAt),
        };
        var details = new[]
        {
            new WikipediaVolumeService.StoredDetail("1", "2020-01-02", "9781234567897"),
            new WikipediaVolumeService.StoredDetail("4", "2099-05", null),
            new WikipediaVolumeService.StoredDetail("5", "2024-13", null),
        };

        var volumes = ExportVolumes.Project(maps, details, wpAt, Today)!;

        Assert.Equal("merged", volumes.Source);
        Assert.Equal(wpAt, volumes.FetchedAt);
        Assert.Equal(["1", "2", "3", "4"], volumes.Items.Select(v => v.Volume));
        var v1 = volumes.Items[0];
        Assert.Equal(("1", "3", "2020-01-02", "released", "9781234567897"), (v1.Chapters!.From, v1.Chapters.To, v1.EnglishDate, v1.EnglishDateKind, v1.Isbn));
        Assert.Equal(["mangadex", "wikipedia"], v1.Sources);
        Assert.Equal(("4", "7"), (volumes.Items[1].Chapters!.From, volumes.Items[1].Chapters!.To)); // Wikipedia completed chapter 7
        Assert.Equal(["mangadex", "wikipedia"], volumes.Items[1].Sources);
        Assert.Equal(("8", "10.5"), (volumes.Items[2].Chapters!.From, volumes.Items[2].Chapters!.To));
        Assert.Equal(["wikipedia"], volumes.Items[2].Sources);
        var v4 = volumes.Items[3];
        Assert.Null(v4.Chapters);
        Assert.Equal(("2099-05", "announced"), (v4.EnglishDate, v4.EnglishDateKind));
    }

    [Fact]
    public void Volumes_FromMangaDexAlone_AreMangaDex_AndNothingStoredIsNull()
    {
        var at = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var volumes = ExportVolumes.Project([Map(VolumeMapSource.MangaDexAggregate, "[{\"v\":\"1\",\"c\":[\"1\",\"291.999\"]}]", at)], [], null, Today)!;
        Assert.Equal(("mangadex", at), (volumes.Source, volumes.FetchedAt));
        Assert.Equal([("1", "1", "291.999")], volumes.Items.Select(v => (v.Volume, v.Chapters!.From, v.Chapters.To)));
        Assert.Equal(["mangadex"], volumes.Items[0].Sources);

        Assert.Null(ExportVolumes.Project([], [], null, Today));
        // A ratio-only map (AniList) is no per-volume list.
        Assert.Null(ExportVolumes.Project([new SeriesVolumeMapEntity { Source = (int)VolumeMapSource.AniListRatio, State = (int)VolumeMapState.Ok, ChaptersPerVolume = 9, ContentHash = "h" }], [], null, Today));
    }

    [Theory]
    [InlineData("2026-10-03", true)]
    [InlineData("2026-10-04", false)]
    [InlineData("2026-09", true)]
    [InlineData("2026-10", false)]
    [InlineData("2025", true)]
    [InlineData("2026", false)]
    [InlineData("2026-02-30", false)]
    public void ReleasedMeansTheWholeDateIsNotAfterToday(string date, bool released)
    {
        Assert.Equal(released, ExportVolumes.IsReleased(date, Today));
    }

    [Fact]
    public void Cursor_RoundTrips_AndRefusesGarbage()
    {
        var cursor = new ExportCursor(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero).UtcTicks, 4711);
        var text = cursor.Encode();
        Assert.DoesNotContain('=', text);
        Assert.True(ExportCursor.TryDecode(text, out var back));
        Assert.Equal(cursor, back);
        Assert.False(ExportCursor.TryDecode("not-a-cursor!", out _));
        Assert.False(ExportCursor.TryDecode("AAAA", out _));
        Assert.False(ExportCursor.TryDecode(new string('A', 40), out _));
        Assert.False(ExportCursor.TryDecode(null, out _));
    }

    [Fact]
    public void Include_DefaultsToEveryBlock_AndRefusesUnknownNames()
    {
        Assert.True(ExportService.TryParseInclude(null, out var all));
        Assert.Equal(["completion", "duplicates", "refresh", "volumes"], all.Order());
        Assert.True(ExportService.TryParseInclude("volumes, refresh", out var two));
        Assert.Equal(["refresh", "volumes"], two.Order());
        Assert.True(ExportService.TryParseInclude("", out var none));
        Assert.Empty(none);
        Assert.False(ExportService.TryParseInclude("volumes,paths", out _));
    }

    private static ExportItemDto Item(DateTimeOffset updatedAt, DateTimeOffset computedAt, string answer = "HaveItAll") => new()
    {
        NodeId = "n1",
        NodeKind = "folder",
        Trail = ["Series"],
        UpdatedAt = updatedAt,
        Link = new ExportLinkDto { State = "Confirmed", UpdatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) },
        Companions = new ExportCompanionsDto(),
        OfficialLinks = [],
        Completion = new ExportCompletionDto
        {
            Answer = answer,
            Reason = "None",
            UpgradeAvailable = false,
            UpgradeVolumes = [],
            ComputedAt = computedAt,
            BasedOnScanAt = computedAt,
        },
    };

    [Fact]
    public void Fingerprint_IgnoresTheRebuildTimes_ButNotTheContent()
    {
        var t1 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddDays(1);
        Assert.Equal(ExportJson.Fingerprint(Item(t1, t1)), ExportJson.Fingerprint(Item(t2, t2)));
        Assert.NotEqual(ExportJson.Fingerprint(Item(t1, t1)), ExportJson.Fingerprint(Item(t1, t1, "MissingSome")));
        Assert.Matches("^[0-9a-f]{64}$", ExportJson.Fingerprint(Item(t1, t1)));
    }

    [Fact]
    public void Json_WritesUtcMillisecondsAndEveryNull()
    {
        var at = new DateTimeOffset(2026, 10, 3, 14, 0, 0, 123, TimeSpan.FromHours(2)).AddTicks(4567);
        var json = ExportJson.Serialize(Item(ExportJson.Truncate(at), ExportJson.Truncate(at)));
        Assert.Contains("\"updatedAt\":\"2026-10-03T12:00:00.123Z\"", json, StringComparison.Ordinal);
        Assert.Contains("\"record\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"carriedFrom\":null", json, StringComparison.Ordinal);
        Assert.Equal(ExportJson.Truncate(at), ExportJson.ReadItem(json)!.UpdatedAt);
    }

    [Fact]
    public void Vocabulary_IsTheContract()
    {
        Assert.Equal(["Confirmed", "Auto", "NeedsReview", "DontMatch"],
            new[] { SeriesLinkState.Confirmed, SeriesLinkState.Auto, SeriesLinkState.NeedsReview, SeriesLinkState.DontMatch }.Select(ExportVocabulary.LinkState));
        Assert.Equal(["CantTell", "HaveItAll", "FinishedMissing", "UpToDate", "MissingSome"], Enum.GetValues<SeriesAnswer>().Select(ExportVocabulary.Answer));
        Assert.All(Enum.GetValues<SeriesAnswerReason>(), r => Assert.Equal(r.ToString(), ExportVocabulary.Reason(r)));
        Assert.Equal(["search", "reference", "comicInfo", "auto"], Enum.GetValues<MetadataMatchMethod>().Select(m => ExportVocabulary.Method((int)m)));
        Assert.All(MangaDexProvider.OfficialLinkKeys, k => Assert.NotNull(ExportVocabulary.OfficialLink(k, "https://example.com/")));
    }
}
