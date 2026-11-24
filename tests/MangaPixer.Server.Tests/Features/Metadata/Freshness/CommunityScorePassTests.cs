namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Freshness;

using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Service-with-DB tests of the 1.54.0 community scores (owner: MangaUpdates + MangaDex + MyAnimeList, consent main 8 / automatic 6):
/// the MangaDex statistics pass (exactly what it sends, what it stores, when it asks again, the gate) and the one-time MangaUpdates
/// catch-up (a day's share of records without a score is refreshed by id, then never again). Synthetic ids, fake HTTP handlers only.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class CommunityScorePassTests : IAsyncLifetime
{
    private const string MdA = "00000000-0000-4000-8000-0000000000a1";
    private const string MdB = "00000000-0000-4000-8000-0000000000b1";

    private MetadataTestDb _t = null!;
    private VolumePassHarness _h = null!;

    /// <summary>MangaDex statistics by manga id: (bayesian, distribution counts); an id missing here is not returned.</summary>
    private readonly Dictionary<string, (double Bayesian, int[] Distribution)> _stats = new(StringComparer.Ordinal);

    public async Task InitializeAsync()
    {
        _t = await MetadataTestDb.CreateAsync();
        _h = new VolumePassHarness(_t);
        _h.Override = Route;
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _t.DisposeAsync();
    }

    private HttpResponseMessage? Route(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        if (uri.Host != MetadataHttp.MangaDexApiHost || uri.AbsolutePath != "/statistics/manga")
            return null;
        var ids = Uri.UnescapeDataString(uri.Query).TrimStart('?').Split('&').Select(p => p["manga[]=".Length..]).ToList();
        var statistics = ids.Where(_stats.ContainsKey).ToDictionary(id => id, id => (object)new
        {
            comments = new { threadId = 1, repliesCount = 2 },
            rating = new
            {
                average = _stats[id].Bayesian + 0.1,
                bayesian = _stats[id].Bayesian,
                distribution = _stats[id].Distribution.Select((n, i) => (Key: (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), n))
                    .ToDictionary(x => x.Key, x => x.n),
            },
            follows = 999,
        });
        return ScriptedHandler.Json(JsonSerializer.Serialize(new { result = "ok", statistics }));
    }

    private DateTimeOffset Now => _h.Time.GetUtcNow();

    private CommunityScorePass Pass()
    {
        ILogger<T> Log<T>() => _h.Auto.Net.LoggerFactory.CreateLogger<T>();
        var mangaDex = new MangaDexProvider(_h.Auto.Net.HttpFactory);
        var sweep = new LatestUploadSweep(_t.Db, _h.Auto.Net.Gateway(), mangaDex, _h.Auto.Service(), _h.Time, Log<LatestUploadSweep>());
        return new CommunityScorePass(_t.Db, _h.Auto.Net.Gateway(), mangaDex, _h.Auto.Service(), sweep, _h.Time, Log<CommunityScorePass>());
    }

    /// <summary>A folder linked Confirmed to a MangaUpdates record whose MangaDex companion is <paramref name="mangaId"/>.</summary>
    private async Task<MetadataRecordEntity> SeriesAsync(string muId, string mangaId)
    {
        var folder = await _t.AddFolderAsync(null, "Synthetic Shelf " + muId);
        await _t.AddArchiveAsync(folder, $"Synthetic Shelf {muId} c001.cbz");
        var record = await _t.AddRecordAsync(muId, "Synthetic Series " + muId);
        record.FetchedAt = Now - TimeSpan.FromDays(1);
        record.CommunityScoreAt = Now - TimeSpan.FromDays(1); // read with its last fetch: not part of the catch-up
        var md = new MetadataRecordEntity
        {
            PublicId = "md" + muId,
            Provider = MetadataProviderAllowlist.MangaDex,
            ExternalId = mangaId,
            Title = "Synthetic Series " + muId,
            FetchedAt = Now - TimeSpan.FromDays(1),
        };
        _t.Db.MetadataRecords.Add(md);
        await _t.Db.SaveChangesAsync();
        _t.Db.MetadataCompanions.Add(new MetadataCompanionEntity
        {
            RecordId = record.Id,
            Provider = MetadataProviderAllowlist.MangaDex,
            CompanionRecordId = md.Id,
            State = (int)CompanionState.Auto,
            Method = (int)CompanionMethod.CrossLink,
            CheckedAt = Now - TimeSpan.FromDays(1),
            NextCheckAt = Now + TimeSpan.FromDays(20),
        });
        await _t.Db.SaveChangesAsync();
        await _t.AddLinkAsync(folder, record, SeriesLinkState.Confirmed);
        return md;
    }

    private async Task<MetadataRecordEntity> RecordAsync(long id)
    {
        _t.Db.ChangeTracker.Clear();
        return await _t.Db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private IReadOnlyList<Uri> StatisticsRequests() =>
        _h.Handler.Seen.Where(s => s.Uri.Host == MetadataHttp.MangaDexApiHost && s.Uri.AbsolutePath == "/statistics/manga").Select(s => s.Uri).ToList();

    [Fact]
    public async Task Statistics_SendsOnlyTheIds_StoresTheBayesianRatingAndVotes_AndAsksAgainAfter30Days()
    {
        await _h.Auto.EnableAutomaticAsync();
        var a = await SeriesAsync("701", MdA);
        var b = await SeriesAsync("702", MdB);
        _stats[MdA] = (7.912, [0, 0, 1, 2, 3, 4, 10, 20, 30, 30]);
        _stats[MdB] = (0, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]); // not rated
        var t0 = Now;

        Assert.True(await Pass().IsDueAsync());
        Assert.Equal(new CommunityScorePassResult(1, 2, 1, null), await Pass().RunAsync());

        var sent = Assert.Single(StatisticsRequests());
        var query = Uri.UnescapeDataString(sent.Query).TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToList();
        Assert.All(query, p => Assert.Equal("manga[]", p[0])); // the record ids, nothing else
        Assert.Equal([MdA, MdB], query.Select(p => p[1]).Order(StringComparer.Ordinal));
        Assert.Equal(MetadataHttp.UserAgent, _h.Handler.Seen.Single(s => s.Uri == sent).Headers["User-Agent"]);
        var ra = await RecordAsync(a.Id);
        Assert.Equal((7.91, 100, t0), (ra.CommunityScore!.Value, ra.CommunityVotes!.Value, ra.CommunityScoreAt!.Value));
        var rb = await RecordAsync(b.Id);
        Assert.Equal((null, null, t0), (rb.CommunityScore, rb.CommunityVotes, rb.CommunityScoreAt)); // checked, no score
        Assert.False(await Pass().IsDueAsync()); // every 6 hours

        // 6 hours later: due, but nothing is stale - nothing is sent.
        _h.Time.Advance(TimeSpan.FromHours(6));
        Assert.Equal(new CommunityScorePassResult(0, 0, 0, null), await Pass().RunAsync());
        Assert.Single(StatisticsRequests());

        // 30 days on: read again.
        _h.Time.Advance(CommunityScorePass.MaxAge);
        _stats[MdA] = (8.05, [0, 0, 0, 0, 0, 0, 0, 0, 50, 51]);
        Assert.Equal(2, (await Pass().RunAsync())!.Records);
        Assert.Equal((8.05, 101), ((await RecordAsync(a.Id)).CommunityScore!.Value, (await RecordAsync(a.Id)).CommunityVotes!.Value));
    }

    [Fact]
    public async Task Statistics_WithTheAutomaticGateClosed_SendsNothing()
    {
        await _h.Auto.EnableAutomaticAsync(automatic: false);
        await SeriesAsync("703", MdA);
        _stats[MdA] = (7.5, [1, 1, 1, 1, 1, 1, 1, 1, 1, 1]);

        Assert.Null(await Pass().RunAsync());
        Assert.Empty(StatisticsRequests());
    }

    [Fact]
    public async Task CatchUp_RefreshesLinkedMangaUpdatesRecordsWithoutAScore_ByIdOnly_OnceADay_ThenStops()
    {
        // Records fetched before 1.54.0 (no score read) and fetched recently - not due on their own cadence.
        var ids = new List<long>();
        foreach (var external in new[] { "811", "812" })
        {
            var folder = await _t.AddFolderAsync(null, "Folder " + external);
            var record = await _t.AddRecordAsync(external, "Record " + external);
            record.OriginStatus = (int)MetadataOriginStatus.Ongoing;
            record.FetchedAt = Now - TimeSpan.FromDays(3);
            record.CommunityScoreAt = null; // fetched before 1.54.0
            await _t.Db.SaveChangesAsync();
            await _t.AddLinkAsync(folder, record, SeriesLinkState.Confirmed);
            _h.Auto.Records[long.Parse(external, System.Globalization.CultureInfo.InvariantCulture)] =
                MuJson.Get(long.Parse(external, System.Globalization.CultureInfo.InvariantCulture), "Record " + external, rating: 8.47);
            ids.Add(record.Id);
        }
        await _h.Auto.EnableAutomaticAsync();

        Assert.Equal(2, (await _h.Auto.Refresh().RunPassAsync()).Refreshed);
        Assert.Equal(["/v1/series/811", "/v1/series/812"], _h.Handler.Seen.Select(s => s.Uri.AbsolutePath).Order().ToArray());
        Assert.All(_h.Handler.Seen, s => Assert.Equal(HttpMethod.Get, s.Method)); // by id: no name is sent
        Assert.All(ids, id => Assert.Equal(8.47, RecordAsync(id).Result.CommunityScore));

        // Every record has a score: the catch-up is done for good; a later pass refreshes nothing early.
        _h.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, (await _h.Auto.Refresh().RunPassAsync()).Refreshed);
        _t.Db.ChangeTracker.Clear();
        Assert.Equal("ok", (await _t.Db.JobRuns.AsNoTracking().SingleAsync(j => j.Key == "metadata-community-score-catchup")).LastOutcome);
    }
}
