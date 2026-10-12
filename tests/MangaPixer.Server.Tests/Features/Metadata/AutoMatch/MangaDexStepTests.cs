namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Synthetic MangaDex JSON (1.42.0): a <c>/manga</c> record with exactly the fields <see cref="MangaDexProvider.ReadManga"/> reads -
/// titles, links, tags, status, year, the main cover and (with the record includes) authors and artists. Public-looking but invented titles.
/// </summary>
public static class MdJson
{
    public sealed record Manga(
        string Id, string Title, string[]? Alt = null, string? Mu = null, string? Al = null, string Language = "ja", int Year = 2015,
        string Status = "ongoing", string[]? Tags = null, string? LastVolume = null, string? LastChapter = null, string[]? Authors = null,
        string? Description = "A synthetic description.");

    public static object Item(Manga m) => new
    {
        id = m.Id,
        type = "manga",
        attributes = new
        {
            title = new Dictionary<string, string> { ["en"] = m.Title },
            altTitles = (m.Alt ?? []).Select(a => new Dictionary<string, string> { ["ja-ro"] = a }).ToArray(),
            description = m.Description is null ? (object)Array.Empty<object>() : new Dictionary<string, string> { ["en"] = m.Description },
            links = m.Mu is null && m.Al is null ? (object)Array.Empty<object>()
                : new Dictionary<string, string?> { ["mu"] = m.Mu, ["al"] = m.Al }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value),
            originalLanguage = m.Language,
            lastVolume = m.LastVolume,
            lastChapter = m.LastChapter,
            status = m.Status,
            year = m.Year,
            tags = (m.Tags ?? []).Select(t => new
            {
                id = Guid.NewGuid().ToString(),
                type = "tag",
                attributes = new { name = new Dictionary<string, string> { ["en"] = t }, group = t is "Action" or "Comedy" or "Drama" ? "genre" : "format" },
            }).ToArray(),
            createdAt = "2019-01-01T00:00:00+00:00",
        },
        relationships = new object[]
        {
            new { id = "c0000000-0000-4000-8000-" + m.Id[^12..], type = "cover_art", attributes = new { fileName = "cover-" + m.Id[..8] + ".jpg" } },
        }.Concat((m.Authors ?? ["Synthetic Mangaka"]).Select((a, i) => (object)new
        {
            id = "a0000000-0000-4000-8000-00000000000" + i, type = "author", attributes = new { name = a },
        })).ToArray(),
    };

    public static string Search(params Manga[] items) =>
        JsonSerializer.Serialize(new { result = "ok", response = "collection", data = items.Select(Item).ToArray(), limit = 10, offset = 0, total = items.Length });

    public static string Entity(Manga m) => JsonSerializer.Serialize(new { result = "ok", response = "entity", data = Item(m) });
}

/// <summary>
/// 1.42.0 (lane D, owner-approved): the MangaDex step of automatic matching - service-with-DB over the real gateway and the real MangaDex
/// provider classes, a scripted handler for MangaUpdates and MangaDex. Pins what is sent (the cleaned name, at most two searches, the fixed
/// includes, nothing for Don't match / Collection about / below the consent / with the setting off / while MangaUpdates had a candidate),
/// that anything it finds waits in Needs review (never Auto), the MangaUpdates bridge, the server-side doujinshi drop, and that the admin's
/// "Match this library now" with "retry unmatched" sends the 1.41.0 unmatched works through the step (owner Q3).
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class MangaDexStepTests : IAsyncLifetime
{
    private const string LanternId = "5e1a7c00-0000-4000-8000-00000000a001";
    private const string LanternFanId = "5e1a7c00-0000-4000-8000-00000000a002";
    private const string BridgeId = "5e1a7c00-0000-4000-8000-00000000a003";
    private const string DoujinId = "5e1a7c00-0000-4000-8000-00000000a004";
    private const long BridgeMu = 61508275290;

    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;
    private DateTimeOffset _since;

    /// <summary>MangaDex search answers by exact title text; unknown titles answer nothing.</summary>
    private readonly Dictionary<string, MdJson.Manga[]> _md = new(StringComparer.OrdinalIgnoreCase);

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new AutoMatchHarness(_db);
        _h.Net.Registry = new MetadataProviderRegistry([_h.Net.Provider, new MangaDexSeriesProvider(new MangaDexProvider(_h.Net.HttpFactory))]);
        var mu = _h.Handler.Respond!;
        _h.Handler.Respond = request => request.RequestUri!.Host == MetadataHttp.MangaDexApiHost ? MangaDex(request) : mu(request);
        _since = DateTimeOffset.UtcNow.AddMinutes(-1);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private HttpResponseMessage MangaDex(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        if (uri.AbsolutePath == "/manga")
        {
            var title = Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('&')[0]["title=".Length..]);
            return ScriptedHandler.Json(MdJson.Search(_md.TryGetValue(title, out var hits) ? hits : []));
        }
        var id = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
        var found = _md.Values.SelectMany(v => v).FirstOrDefault(m => m.Id == id);
        return found is null
            ? ScriptedHandler.Json("{\"result\":\"error\"}", HttpStatusCode.NotFound)
            : ScriptedHandler.Json(MdJson.Entity(found));
    }

    private IReadOnlyList<SeenRequest> MangaDexRequests() => _h.Handler.Seen.Where(s => s.Uri.Host == MetadataHttp.MangaDexApiHost).ToList();

    private async Task<CatalogNodeEntity> SeriesAsync(string name, CatalogNodeEntity? parent = null)
    {
        var folder = await _db.AddFolderAsync(parent, name);
        await _db.AddArchiveAsync(folder, $"{name} v01");
        await _db.AddArchiveAsync(folder, $"{name} v02");
        return folder;
    }

    private async Task<int> MatchNewFoldersAsync()
    {
        await _h.Service().EnqueueNewFoldersAsync(_db.LibraryId, _since);
        return await _h.DrainAsync();
    }

    private Task<NodeSeriesLinkEntity?> LinkOfAsync(CatalogNodeEntity node) =>
        _db.Db.NodeSeriesLinks.AsNoTracking().FirstOrDefaultAsync(l => l.NodeId == node.Id);

    private Task<List<MetadataMatchCandidateEntity>> CandidatesOfAsync(CatalogNodeEntity node) =>
        _db.Db.MetadataMatchCandidates.AsNoTracking().Where(c => c.NodeId == node.Id).OrderBy(c => c.Rank).ToListAsync();

    [Fact]
    public async Task AWorkMangaUpdatesFindsNothingFor_IsSearchedOnMangaDexByItsCleanedName_AndWaitsInNeedsReview_NeverAuto()
    {
        await _h.EnableAutomaticAsync();
        // An exact title on MangaDex that the matcher alone would link automatically.
        _md["Silver Lantern Tales"] = [new MdJson.Manga(LanternId, "Silver Lantern Tales", Tags: ["Action"])];
        var folder = await SeriesAsync("Silver Lantern Tales [Scans]");

        await MatchNewFoldersAsync();

        var link = await LinkOfAsync(folder);
        Assert.NotNull(link);
        Assert.Equal((int)SeriesLinkState.NeedsReview, link!.State);
        Assert.Null(link.RecordId);
        var candidate = Assert.Single(await CandidatesOfAsync(folder));
        Assert.Equal(MangaDexProvider.ProviderId, candidate.Provider);
        Assert.Equal(LanternId, candidate.ExternalId);
        Assert.Equal(1.0, candidate.TitleScore);

        // What was sent: MangaUpdates first, then ONE MangaDex search with the cleaned name and the fixed parameters - no GET.
        var md = Assert.Single(MangaDexRequests());
        Assert.Equal(MangaDexProvider.SearchUrl("Silver Lantern Tales", 0, record: true), md.Uri.AbsoluteUri);
        Assert.Contains("includes%5B%5D=author&includes%5B%5D=artist", md.Uri.Query, StringComparison.Ordinal);
        var muIndex = _h.Handler.Seen.ToList().FindIndex(s => s.Uri.Host == MetadataHttp.MangaUpdatesApiHost);
        Assert.True(muIndex >= 0 && muIndex < _h.Handler.Seen.ToList().FindIndex(s => s.Uri.Host == MetadataHttp.MangaDexApiHost));
        Assert.Empty(await _db.Db.MetadataRecords.AsNoTracking().Where(r => r.Provider == MangaDexProvider.ProviderId).ToListAsync());
        Assert.Contains(_h.Net.Logs.Lines, l => l.Contains("asked MangaDex for node", StringComparison.Ordinal));
        Assert.DoesNotContain(_h.Net.Logs.Lines, l => l.Contains("Lantern", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoMangaDexRequest_WhileMangaUpdatesHasACandidateAtTheReviewFloor()
    {
        await _h.EnableAutomaticAsync();
        _h.Search["Silver Lantern Tales"] = [new MuJson.Hit(9001, "Silver Lantern Tale Stories")];
        _h.Records[9001] = MuJson.Get(9001, "Silver Lantern Tale Stories");
        _md["Silver Lantern Tales"] = [new MdJson.Manga(LanternId, "Silver Lantern Tales")];
        var folder = await SeriesAsync("Silver Lantern Tales");

        await MatchNewFoldersAsync();

        Assert.NotNull(await LinkOfAsync(folder));
        Assert.Empty(MangaDexRequests());
    }

    [Fact]
    public async Task UnderDontMatch_NothingIsSentToMangaDex()
    {
        await _h.EnableAutomaticAsync();
        _md["Silver Lantern Tales"] = [new MdJson.Manga(LanternId, "Silver Lantern Tales")];
        var parent = await _db.AddFolderAsync(null, "Kept Apart");
        await _db.AddLinkAsync(parent, null, SeriesLinkState.DontMatch);
        await SeriesAsync("Silver Lantern Tales", parent);

        await MatchNewFoldersAsync();

        Assert.Empty(MangaDexRequests());
    }

    [Fact]
    public async Task InsideACollectionAboutFolder_NothingIsSentToMangaDex()
    {
        await _h.EnableAutomaticAsync();
        var record = await _db.AddRecordAsync("9100", "Host Series");
        var collection = await _db.AddFolderAsync(null, "Collection Host Series");
        await _db.AddArchiveAsync(collection, "Silver Lantern Tales");
        await _db.AddArchiveAsync(collection, "Golden Lantern Tales");
        await _db.AddLinkAsync(collection, record, SeriesLinkState.CollectionAbout);
        _md["Silver Lantern Tales"] = [new MdJson.Manga(LanternId, "Silver Lantern Tales")];

        await _h.Service().EnqueueBelowAsync(_db.LibraryId, collection.Id);
        await _h.DrainAsync();

        Assert.Contains(_h.Handler.Seen, s => s.Uri.Host == MetadataHttp.MangaUpdatesApiHost);
        Assert.Empty(MangaDexRequests());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BelowTheNewAutomaticConsent_OrWithTheSettingOff_NoMangaDexRequest(bool settingOff)
    {
        if (settingOff)
        {
            await _h.EnableAutomaticAsync();
            await _db.Db.AppSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.MetadataMangaDexStepEnabled, false));
        }
        else
        {
            await _h.EnableAutomaticAsync(autoConsentVersion: MetadataAutoConsent.CurrentVersion - 1);
        }
        _md["Silver Lantern Tales"] = [new MdJson.Manga(LanternId, "Silver Lantern Tales")];
        var folder = await SeriesAsync("Silver Lantern Tales");

        await MatchNewFoldersAsync();

        Assert.Empty(MangaDexRequests());
        if (settingOff)
            Assert.Null(await LinkOfAsync(folder)); // MangaUpdates found nothing: Unmatched, as before 1.42.0.
        else
            Assert.Empty(_h.Handler.Seen); // The old automatic consent sends nothing at all.
    }

    [Fact]
    public async Task AMangaDexHitThatLinksAMangaUpdatesSeries_ProposesThatMangaUpdatesRecord_InNeedsReview()
    {
        await _h.EnableAutomaticAsync();
        _md["Silver Lantern Tales"] = [new MdJson.Manga(BridgeId, "Silver Lantern Tales", Mu: MangaUpdatesReference.ToBase36(BridgeMu))];
        // MangaUpdates lists the series under another name (its search does not find the folder's name).
        _h.Records[BridgeMu] = MuJson.Get(BridgeMu, "Gin no Rantan Monogatari");
        var folder = await SeriesAsync("Silver Lantern Tales");

        await MatchNewFoldersAsync();

        var link = await LinkOfAsync(folder);
        Assert.Equal((int)SeriesLinkState.NeedsReview, link!.State);
        var candidate = Assert.Single(await CandidatesOfAsync(folder));
        Assert.Equal(MangaUpdatesProvider.ProviderId, candidate.Provider);
        Assert.Equal(BridgeMu.ToString(System.Globalization.CultureInfo.InvariantCulture), candidate.ExternalId);
        // The MangaUpdates record was read by its id only (the bridge), after the MangaDex search.
        Assert.Contains(_h.Handler.Seen, s => s.Uri.Host == MetadataHttp.MangaUpdatesApiHost && s.Uri.AbsolutePath.EndsWith("/" + BridgeMu, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADoujinshiTaggedHit_IsDroppedOnTheServer_UnlessTheFolderContentAllowsIt()
    {
        await _h.EnableAutomaticAsync();
        _md["Silver Lantern Tales"] =
        [
            new MdJson.Manga(DoujinId, "Silver Lantern Tales", Tags: ["Doujinshi"]),
            new MdJson.Manga(LanternFanId, "Silver Lantern Tales Anthology"),
        ];
        var folder = await SeriesAsync("Silver Lantern Tales");

        await MatchNewFoldersAsync();

        var candidates = await CandidatesOfAsync(folder);
        Assert.DoesNotContain(candidates, c => c.ExternalId == DoujinId);
        // Nothing about doujinshi is sent: the request is the plain search.
        Assert.DoesNotContain(MangaDexRequests(), s => s.Uri.Query.Contains("Tags", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RetryUnmatched_InMatchThisLibraryNow_SendsAnUnmatchedWorkWithNoRetryLeft_ThroughTheMangaDexStep()
    {
        // Owner Q3: a work left Unmatched before 1.42.0, its automatic retries used up, gets the MangaDex step only when an admin asks.
        await _h.EnableAutomaticAsync();
        var folder = await SeriesAsync("Silver Lantern Tales");
        await _db.Db.AppSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.MetadataMangaDexStepEnabled, false));
        await MatchNewFoldersAsync();
        await _db.Db.MetadataMatchQueue.Where(q => q.NodeId == folder.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.RetryStep, AutoMatchPolicy.UnmatchedRetry.Length).SetProperty(q => q.NotBefore, (DateTimeOffset?)null));
        var row = await _db.Db.MetadataMatchQueue.AsNoTracking().SingleAsync(q => q.NodeId == folder.Id);
        Assert.Equal((int)MatchBand.Unmatched, row.Outcome);
        await _db.Db.AppSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.MetadataMangaDexStepEnabled, true));
        _md["Silver Lantern Tales"] = [new MdJson.Manga(LanternId, "Silver Lantern Tales")];

        // Nothing happens on its own: no retry is due.
        Assert.Equal(0, await _h.Service().PromoteDueRetriesAsync());
        Assert.Equal(0, await _h.DrainAsync());
        Assert.Empty(MangaDexRequests());

        var (error, _) = await _h.Service().StartBulkAsync(_db.LibraryPublicId, new MetadataMatchLibraryRequest { RetryUnmatched = true }, "admin");
        Assert.Null(error);
        await _h.DrainAsync();

        Assert.Single(MangaDexRequests());
        Assert.Equal((int)SeriesLinkState.NeedsReview, (await LinkOfAsync(folder))!.State);
        Assert.Equal(LanternId, Assert.Single(await CandidatesOfAsync(folder)).ExternalId);
    }

    [Fact]
    public async Task AtMostTwoMangaDexSearchesPerWork()
    {
        await _h.EnableAutomaticAsync();
        var folder = await _db.AddFolderAsync(null, "Silver Lantern Tales");
        await _db.AddArchiveAsync(folder, "Silver Lantern Tales v01");
        await _db.AddArchiveAsync(folder, "Silver Lantern Tales v02");
        await _db.AddComicInfoAsync(await _db.Db.CatalogNodes.FirstAsync(n => n.ParentId == folder.Id), series: "Lantern Tales of Silver");

        await MatchNewFoldersAsync();

        Assert.InRange(MangaDexRequests().Count, 1, AutoMatchLookup.MaxMangaDexSearchesPerWork);
        Assert.Null(await LinkOfAsync(folder)); // MangaDex found nothing either: Unmatched.
    }
}
