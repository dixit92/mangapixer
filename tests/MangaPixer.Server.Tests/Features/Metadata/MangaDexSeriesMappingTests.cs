namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.42.0 (lane D): unit tests of MangaDex as a series record - the record reader (authors, genres, tags, description, relations from the
/// same answer), the ONE mapping onto a provider record, the request addresses (what is sent), and the <c>links.mu</c> decode the bridge and
/// the upgrade path use. Synthetic JSON only.
/// </summary>
public sealed class MangaDexSeriesMappingTests
{
    private const string Id = "5e1a7c00-0000-4000-8000-00000000c001";

    private static MangaDexManga Read(MdJson.Manga manga)
    {
        using var doc = JsonDocument.Parse(MdJson.Entity(manga));
        return MangaDexProvider.ReadManga(doc.RootElement.GetProperty("data"))!;
    }

    [Fact]
    public void ARecordAnswer_GivesTheSeriesFields_FromTheSameAnswer()
    {
        var m = Read(new MdJson.Manga(Id, "Silver Lantern Tales", Alt: ["Gin no Rantan"], Mu: MangaUpdatesReference.ToBase36(61508275290), Al: "777",
            Language: "ko", Year: 2018, Status: "completed", Tags: ["Action", "Long Strip", "Oneshot"], LastVolume: "3", LastChapter: "41.5",
            Authors: ["Synthetic Writer", "Synthetic Writer"], Description: "Line one.\n\n[A link](https://example.com)"));

        Assert.Equal(["Action"], m.Genres);
        Assert.True(m.LongStrip);
        Assert.True(m.Oneshot);
        Assert.False(m.Doujinshi);
        Assert.Single(m.Creators); // a repeated name counts once per role
        Assert.Equal("Line one.", m.Description);

        var r = MangaDexMapping.ToRecord(m);
        Assert.Equal(MangaDexProvider.ProviderId, r.Provider);
        Assert.Equal(Id, r.ExternalId);
        Assert.Equal(MetadataOrigin.Korea, r.Origin);
        Assert.Equal(MetadataFormat.Comic, r.Format);
        Assert.True(r.Webtoon);
        Assert.Null(r.ProviderType);
        Assert.Equal(MetadataOriginStatus.Complete, r.OriginStatus);
        Assert.Equal(3, r.OriginVolumes);
        Assert.Null(r.LatestChapter); // never from a GET: the released-in-language list sets it
        Assert.Equal(41, r.TotalChapters); // the FINAL chapter of a finished series
        Assert.Equal("61508275290", r.CrossIds[MetadataProviderAllowlist.MangaUpdates]);
        Assert.Equal("777", r.CrossIds[MetadataProviderAllowlist.AniList]);
        Assert.Equal("https://mangadex.org/title/" + Id, r.SiteUrl);
        Assert.StartsWith("https://uploads.mangadex.org/covers/" + Id + "/", r.ImageRemoteUrl, StringComparison.Ordinal);
        Assert.EndsWith(".512.jpg", r.ImageRemoteUrl, StringComparison.Ordinal);
        Assert.Equal("41.5", CompanionLinkService.MangaDexExtra.Read(r.ExtraJson).LastChapter);
    }

    [Theory]
    [InlineData("12345", "12345")]
    [InlineData(" 12345 ", "12345")]
    [InlineData("abc", null)]
    [InlineData("0", null)]
    [InlineData("-3", null)]
    [InlineData("+5", null)]
    [InlineData("1.5", null)]
    [InlineData("99999999999", null)] // not an id MAL can have
    [InlineData("", null)]
    public void LinksMal_IsStoredAsTheTrackerCrossId_OnlyWhenAPositiveInteger(string mal, string? expected)
    {
        var m = Read(new MdJson.Manga(Id, "Silver Lantern Tales", Mu: MangaUpdatesReference.ToBase36(61508275290), Al: "777", Mal: mal));
        Assert.Equal(expected, m.MyAnimeListId);

        // Both writers of a MangaDex record row: the series record and the companion (CompanionLinkService.UpsertRecordAsync).
        foreach (var cross in new[] { MangaDexMapping.ToRecord(m).CrossIds, MangaDexMapping.CrossIdsOf(m, "61508275290") })
        {
            Assert.Equal(expected, cross.GetValueOrDefault(com.lifepixer.mangapixer.Core.Trackers.TrackerCrossIdKeys.MyAnimeList));
            Assert.Equal("61508275290", cross[MetadataProviderAllowlist.MangaUpdates]); // the other cross ids are unchanged
            Assert.Equal("777", cross[MetadataProviderAllowlist.AniList]);
            Assert.Equal(expected is null ? 2 : 3, cross.Count);
        }
        Assert.DoesNotContain(m.OfficialLinks, l => l.Key == "mal"); // a database link, never an official source
        Assert.DoesNotContain("\"mal\"", MangaDexMapping.ExtraOf(m), StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordWithoutLinksMal_StoresNoTrackerCrossId()
    {
        var cross = MangaDexMapping.ToRecord(Read(new MdJson.Manga(Id, "Silver Lantern Tales", Al: "777"))).CrossIds;
        Assert.False(cross.ContainsKey(com.lifepixer.mangapixer.Core.Trackers.TrackerCrossIdKeys.MyAnimeList));
    }

    [Fact]
    public void ARunningSeries_HasNoChapterTotal_AndADoujinshiTag_IsTheDoujinshiFormat()
    {
        var r = MangaDexMapping.ToRecord(Read(new MdJson.Manga(Id, "Silver Lantern Tales", Status: "ongoing", LastChapter: "12", Tags: ["Doujinshi"])));
        Assert.Null(r.TotalChapters);
        Assert.Equal(MetadataOriginStatus.Ongoing, r.OriginStatus);
        Assert.Equal(MetadataFormat.Doujinshi, r.Format);
        Assert.Null(r.Webtoon);
    }

    [Theory]
    [InlineData("spin_off", "spin-off")]
    [InlineData("main_story", "main story")]
    [InlineData("colored", "alternate version")]
    [InlineData("same_franchise", "same franchise")]
    public void Relations_UseTheSeriesFamilyWords(string mangaDex, string expected) =>
        Assert.Equal(expected, MangaDexMapping.RelationOf(mangaDex));

    [Theory]
    [InlineData("1a2b3c", true)]       // base36 (today)
    [InlineData("61508275290", true)]  // a long decimal id
    [InlineData("12345", false)]       // a short legacy id never maps to a current one
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheMangaUpdatesLink_DecodesAsTheCrossLinkRuleReadsIt(string? link, bool decodes)
    {
        var id = MangaDexCrossLink.MangaUpdatesIdOf(link);
        Assert.Equal(decodes, id is not null);
        if (id is not null)
            Assert.True(MangaDexCrossLink.LinksTo(link, id));
    }

    [Fact]
    public void WhatIsSent_TheRecordRequestsCarryTheFixedIncludes_TheCompanionsKeepTheirs()
    {
        Assert.Equal("https://api.mangadex.org/manga?title=Silver%20Lantern&limit=10&offset=20&order%5Brelevance%5D=desc"
            + "&contentRating%5B%5D=safe&contentRating%5B%5D=suggestive&contentRating%5B%5D=erotica&contentRating%5B%5D=pornographic"
            + "&includes%5B%5D=cover_art&includes%5B%5D=author&includes%5B%5D=artist",
            MangaDexProvider.SearchUrl("Silver Lantern", 20, record: true));
        Assert.Equal("https://api.mangadex.org/manga?title=Silver%20Lantern&limit=10&order%5Brelevance%5D=desc"
            + "&contentRating%5B%5D=safe&contentRating%5B%5D=suggestive&contentRating%5B%5D=erotica&contentRating%5B%5D=pornographic"
            + "&includes%5B%5D=cover_art",
            MangaDexProvider.SearchUrl("Silver Lantern"));
        Assert.Equal($"https://api.mangadex.org/manga/{Id}?includes%5B%5D=cover_art&includes%5B%5D=author&includes%5B%5D=artist",
            MangaDexProvider.RecordUrl(Id));
        Assert.Contains("offset=990", MangaDexProvider.SearchUrl("x", 5000, record: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSeriesProvider_DropsDoujinshiOnTheServer_OnlyWhenTheSearchHidesThem()
    {
        var handler = new ScriptedHandler
        {
            Respond = _ => ScriptedHandler.Json(MdJson.Search(
                new MdJson.Manga(Id, "Silver Lantern Tales", Tags: ["Doujinshi"]),
                new MdJson.Manga("5e1a7c00-0000-4000-8000-00000000c002", "Silver Lantern Tales Anthology"))),
        };
        var services = new ServiceCollection();
        com.lifepixer.mangapixer.Server.Program.AddMetadataClient(services, MetadataHttp.MangaDexApiClient, MetadataHttp.MangaDexApiHost, "application/json")
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var sp = services.BuildServiceProvider();
        var provider = new MangaDexSeriesProvider(new MangaDexProvider(sp.GetRequiredService<IHttpClientFactory>()));

        var hidden = await provider.SearchSeriesAsync(new(Text: "Silver Lantern Tales", LibraryId: 1, HideDoujinshiAndNovels: true), CancellationToken.None);
        Assert.Single(hidden.Hits);
        var kept = await provider.SearchSeriesAsync(new(Text: "Silver Lantern Tales", LibraryId: 1, HideDoujinshiAndNovels: true, AllowDoujinshi: true), CancellationToken.None);
        Assert.Equal(2, kept.Hits.Count);
        Assert.All(kept.Hits, h => Assert.NotNull(h.Record));
        Assert.All(handler.Seen, s => Assert.DoesNotContain("Doujin", s.Uri.Query, StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith(".256.jpg", kept.Hits[0].ImageRemoteUrl, StringComparison.Ordinal);
    }
}
