namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Volumes.Wikipedia;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes.Wikipedia;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Service-with-DB tests (1.32.0) of the Wikipedia step INSIDE the background volume-cover tick - the way it runs in the app: only series
/// where MangaDex has gaps are asked, the request slice and the daily budget are shared, an admin removing Wikipedia from the allowlist
/// stops it without stopping the MangaDex work, and the lists it stores reach the Volumes view's shared reader. Zero real network.
/// </summary>
public sealed class WikipediaVolumePassTests : IAsyncLifetime
{
    private const string GapId = "99000000001";
    private const string CompleteId = "99000000002";

    private MetadataTestDb _t = null!;
    private VolumePassHarness _h = null!;
    private readonly FakeWikimedia _wiki = new();

    public async Task InitializeAsync()
    {
        _t = await MetadataTestDb.CreateAsync();
        _h = new VolumePassHarness(_t);
        _h.Override = _wiki.Respond;
        await _h.Auto.EnableAutomaticAsync();
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _t.DisposeAsync();
    }

    private static string Base36(string id) => MangaUpdatesReference.ToBase36(long.Parse(id, System.Globalization.CultureInfo.InvariantCulture));

    private sealed class NoHasher : ICoverHasher
    {
        public Task<ulong?> HashFileAsync(string path, CancellationToken ct) => Task.FromResult<ulong?>(null);
    }

    private VolumeCoverPass Pass() => new(
        _t.Db, _h.Auto.Service(), _h.Auto.Net.Settings(), _h.Companions(), _h.Maps(), _h.Fetcher(), new NoHasher(), _h.Thumbnails, new CoverHashCache(),
        _h.PassState, _h.Time, _h.Auto.Net.LoggerFactory.CreateLogger<VolumeCoverPass>(), _h.Decisions,
        wikipedia: new WikipediaVolumeService(_t.Db, _h.Auto.Net.Gateway(), new WikipediaApi(_h.Auto.Net.HttpFactory), _h.Maps(), _h.Time,
            _h.Auto.Net.LoggerFactory.CreateLogger<WikipediaVolumeService>()));

    private async Task<VolumeCoverPassResult> TickAsync()
    {
        _t.Db.ChangeTracker.Clear();
        var result = await Pass().RunTickAsync();
        _t.Db.ChangeTracker.Clear();
        return result;
    }

    private async Task<MetadataRecordEntity> SeriesAsync(string muId, string title, int totalVolumes, int mangaDexVolumes)
    {
        var folder = await _t.AddFolderAsync(null, "Synthetic Shelf " + muId);
        await _t.AddArchiveAsync(folder, "Synthetic Shelf " + muId + " v01.cbz");
        var record = await _t.AddRecordAsync(muId, title);
        record.OriginVolumes = totalVolumes;
        await _t.Db.SaveChangesAsync();
        await _t.AddLinkAsync(folder, record, SeriesLinkState.Auto);
        if (mangaDexVolumes > 0)
        {
            var list = Enumerable.Range(1, mangaDexVolumes)
                .Select(v => new VolumeMapEntry(v.ToString(), Enumerable.Range(((v - 1) * 4) + 1, 4).Select(c => c.ToString()).ToList())).ToList();
            var now = _h.Time.GetUtcNow();
            _t.Db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
            {
                RecordId = record.Id,
                Source = (int)VolumeMapSource.MangaDexAggregate,
                State = (int)VolumeMapState.Ok,
                VolumesJson = VolumeMapJson.Write(list),
                ContentHash = "seed",
                Version = 1,
                FetchedAt = now,
                NextCheckAt = now.AddDays(30),
            });
            await _t.Db.SaveChangesAsync();
        }
        return record;
    }

    private void KnowWikipedia(string muId, string article, int volumes)
    {
        _wiki.Items[Base36(muId)] = ["Q9" + muId[^3..]];
        _wiki.Sitelinks["Q9" + muId[^3..]] = article;
        _wiki.Pages["List of " + article + " chapters"] = new FakeWikimedia.Page(50, FakeWikimedia.ListPage("Volumes", FakeWikimedia.Run(volumes)));
    }

    private IReadOnlyList<Uri> Wikimedia() => _h.Handler.Seen.Where(s => FakeWikimedia.IsWikimedia(s.Uri)).Select(s => s.Uri).ToList();

    [Fact]
    public async Task ATick_AsksWikipediaOnlyAboutTheSeriesWhereMangaDexHasAGap()
    {
        var gap = await SeriesAsync(GapId, "Synthetic Gap", totalVolumes: 6, mangaDexVolumes: 0);
        var complete = await SeriesAsync(CompleteId, "Synthetic Complete", totalVolumes: 3, mangaDexVolumes: 3);
        KnowWikipedia(GapId, "Synthetic Gap", 6);
        KnowWikipedia(CompleteId, "Synthetic Complete", 3);

        var result = await TickAsync();

        Assert.Null(result.WaitingCode);
        var sent = Wikimedia();
        Assert.Equal(3, sent.Count); // the gap series only: item search, sitelink, pages
        Assert.All(sent, u => Assert.DoesNotContain(Base36(CompleteId), u.Query, StringComparison.Ordinal));
        Assert.DoesNotContain(sent, u => Uri.UnescapeDataString(u.Query).Contains("Complete", StringComparison.Ordinal));
        var row = await _t.Db.WikipediaLists.AsNoTracking().SingleAsync();
        Assert.Equal((gap.Id, (int)WikipediaListState.Found), (row.RecordId, row.State));
        Assert.False(await _t.Db.WikipediaLists.AnyAsync(l => l.RecordId == complete.Id));
        // The tick counted the Wikimedia requests in its slice (the MangaDex search for each series + the three of the gap series).
        Assert.True(result.Requests >= 3 + 2);

        // The shared reader of the Volumes view now sees six exact volumes and the credit.
        var maps = await _t.Db.SeriesVolumeMaps.AsNoTracking().Where(m => m.RecordId == gap.Id).ToListAsync();
        var (map, _) = com.lifepixer.mangapixer.Server.Features.Metadata.Reach.SeriesProgressLoader.MapAndFacts(maps, null, "en");
        Assert.Equal((6, VolumeListSource.Wikipedia), (map.Volumes.Count, map.Source));
    }

    [Fact]
    public async Task ASecondTick_BeforeTheCadence_SendsNothingToWikimedia()
    {
        await SeriesAsync(GapId, "Synthetic Gap", totalVolumes: 6, mangaDexVolumes: 0);
        KnowWikipedia(GapId, "Synthetic Gap", 6);
        await TickAsync();
        _h.ResetRequests();

        await TickAsync();

        Assert.Empty(Wikimedia());
    }

    [Fact]
    public async Task WikipediaRemovedFromTheAllowlist_IsSkipped_AndTheMangaDexWorkGoesOn()
    {
        await SeriesAsync(GapId, "Synthetic Gap", totalVolumes: 6, mangaDexVolumes: 0);
        await SeriesAsync(MdFixtures.MuBerserk, "Berserk", totalVolumes: 43, mangaDexVolumes: 0);
        KnowWikipedia(GapId, "Synthetic Gap", 6);
        Assert.Null(await _h.Auto.Net.Settings().UpdateAsync(new UpdateMetadataSettingsRequest { RemovedProviders = [MetadataProviderAllowlist.Wikipedia] }, "admin"));

        var result = await TickAsync();

        Assert.Null(result.WaitingCode);
        Assert.Empty(Wikimedia()); // a removed site receives no request of any kind
        Assert.False(await _t.Db.WikipediaLists.AnyAsync());
        Assert.NotEmpty(_h.MangaDexRequests()); // the MangaDex companion and its lists were still read
        Assert.True(await _t.Db.SeriesVolumeMaps.AnyAsync(m => m.Source == (int)VolumeMapSource.MangaDexAggregate));
    }

    [Fact]
    public async Task ASlowDownFromWikimedia_ClosesTheStepForTheTick_NotTheTick()
    {
        await SeriesAsync(GapId, "Synthetic Gap", totalVolumes: 6, mangaDexVolumes: 0);
        await SeriesAsync(MdFixtures.MuBerserk, "Berserk", totalVolumes: 43, mangaDexVolumes: 0);
        KnowWikipedia(GapId, "Synthetic Gap", 6);
        _wiki.MaxLagSeconds = 5;

        var result = await TickAsync();

        Assert.Null(result.WaitingCode);
        Assert.Single(Wikimedia()); // one request answered "slow down"; the other series were not asked
        Assert.NotEmpty(_h.MangaDexRequests());
        Assert.False(await _t.Db.WikipediaLists.AnyAsync());
    }

    [Fact]
    public async Task WithAutomaticMatchingOff_TheTickWaits_AndNothingIsSent()
    {
        await SeriesAsync(GapId, "Synthetic Gap", totalVolumes: 6, mangaDexVolumes: 0);
        KnowWikipedia(GapId, "Synthetic Gap", 6);
        var settings = await _t.Db.AppSettings.FirstAsync(s => s.Id == AppSettingsEntity.SingletonId);
        settings.MetadataAutoMatchEnabled = false; // Automatic matching off
        await _t.Db.SaveChangesAsync();
        _h.ResetRequests(failOnAnyRequest: true);

        var result = await TickAsync();

        Assert.Equal("automatic_off", result.WaitingCode);
        Assert.Equal(0, _h.Handler.CallCount);
    }
}
