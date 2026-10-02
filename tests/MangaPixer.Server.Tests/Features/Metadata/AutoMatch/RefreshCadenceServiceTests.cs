namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of the 1.32.0 refresh cadence: due selection by each record's stored cadence (the admin's choice and the
/// publishing pace), observations written on change and pruned, the cadence recomputed from a refresh, and the companion checks
/// pulled in when a cadence gets shorter.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class RefreshCadenceServiceTests : IAsyncLifetime
{
    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new AutoMatchHarness(_db);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private async Task<MetadataRecordEntity> LinkedAsync(string externalId, TimeSpan age, int? startYear = null, int? volumes = null,
        MetadataOriginStatus status = MetadataOriginStatus.Ongoing)
    {
        var folder = await _db.AddFolderAsync(null, "Folder " + externalId);
        var record = await _db.AddRecordAsync(externalId, "Record " + externalId);
        record.OriginStatus = (int)status;
        record.StartYear = startYear;
        record.OriginVolumes = volumes;
        record.FetchedAt = _h.Time.GetUtcNow() - age;
        await _db.Db.SaveChangesAsync();
        await _db.AddLinkAsync(folder, record, SeriesLinkState.Confirmed);
        return record;
    }

    private async Task<MetadataRecordEntity> ReloadAsync(long id)
    {
        _db.Db.ChangeTracker.Clear();
        return await _db.Db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    [Fact]
    public async Task AFastSeries_IsCheckedWeekly_ASlowOneMonthly_AndTheRefreshRecomputesItsCadence()
    {
        // Started ten years ago with 70 volumes (a volume every ~2 months) -> weekly; 20 volumes -> monthly.
        var year = _h.Time.GetUtcNow().Year - 10;
        var fast = await LinkedAsync("901", TimeSpan.FromDays(8), year, 70);
        var slow = await LinkedAsync("902", TimeSpan.FromDays(8), year, 20);
        // The provider now says: started 2001, 5 volumes (the synthetic answer) -> slow.
        _h.Records[901] = MuJson.Get(901, "Record 901");
        await _h.EnableAutomaticAsync();

        var result = await _h.Refresh().RunPassAsync();

        Assert.Equal((1, 1, (string?)null), (result.Refreshed, result.Due, result.StoppedCode));
        Assert.Equal(["/v1/series/901"], _h.Handler.Seen.Select(s => s.Uri.AbsolutePath).ToArray());
        Assert.Equal(30, (await ReloadAsync(slow.Id)).RefreshCadenceDays);
        var refreshed = await ReloadAsync(fast.Id);
        Assert.Equal(30, refreshed.RefreshCadenceDays); // recomputed from what the refresh brought
        var observations = await _db.Db.MetadataRecordObservations.Where(o => o.RecordId == fast.Id).OrderBy(o => o.ObservedAt).ToListAsync();
        Assert.Equal(2, observations.Count); // the baseline, then what the refresh saw
        Assert.Equal((70, 5), (observations[0].OriginVolumes, observations[1].OriginVolumes));
    }

    [Fact]
    public async Task AProviderThatRefuses_SkipsOnlyItsOwnRecords_TheOthersAreStillRefreshed()
    {
        // 1.32.0 integration (lanes B + D): a Grand Comics Database record refused (here: GCD removed from the allowed sites; the
        // same path as its slow bucket's provider_busy) must not end the pass - the MangaUpdates record due after it is refreshed.
        var comic = await LinkedAsync("gcd-1", TimeSpan.FromDays(400));
        comic.Provider = MetadataProviderAllowlist.Gcd;
        var manga = await LinkedAsync("903", TimeSpan.FromDays(40));
        await _db.Db.SaveChangesAsync();
        _h.Records[903] = MuJson.Get(903, "Record 903");
        _h.Net.Registry = new MetadataProviderRegistry(
            [_h.Net.Provider, new com.lifepixer.mangapixer.Server.Features.Metadata.Providers.Gcd.GcdProvider(_h.Net.HttpFactory)]);
        await _h.EnableAutomaticAsync();
        Assert.Null(await _h.Net.Settings().UpdateAsync(
            new com.lifepixer.mangapixer.Core.Api.UpdateMetadataSettingsRequest { RemovedProviders = [MetadataProviderAllowlist.Gcd] }, "admin"));

        var result = await _h.Refresh().RunPassAsync();

        Assert.Equal(1, result.Refreshed);
        Assert.Equal("provider_not_allowed", result.StoppedCode);
        Assert.Equal(["/v1/series/903"], _h.Handler.Seen.Select(s => s.Uri.AbsolutePath).ToArray()); // nothing sent to GCD
        Assert.True((await ReloadAsync(manga.Id)).FetchedAt > _h.Time.GetUtcNow() - TimeSpan.FromMinutes(1));
        Assert.Equal(comic.FetchedAt, (await ReloadAsync(comic.Id)).FetchedAt); // untouched: still due next pass
    }

    [Fact]
    public async Task TheAdminsWeeklyChoice_AppliesToEveryOngoingSeries_FinishedOnesKeepTheirs()
    {
        await LinkedAsync("911", TimeSpan.FromDays(8));
        await LinkedAsync("912", TimeSpan.FromDays(8), status: MetadataOriginStatus.Complete);
        _h.Records[911] = MuJson.Get(911, "Record 911");
        await _h.EnableAutomaticAsync();
        var row = await _db.Db.AppSettings.FirstAsync();
        row.MetadataRefreshOngoingDays = 7;
        row.MetadataRefreshFollowPace = false;
        await _db.Db.SaveChangesAsync();

        Assert.Equal(1, (await _h.Refresh().RunPassAsync()).Refreshed);
        Assert.Equal(["/v1/series/911"], _h.Handler.Seen.Select(s => s.Uri.AbsolutePath).ToArray());
    }

    [Fact]
    public async Task Observations_AreWrittenOnlyOnChange_AndKeptToTheLast24()
    {
        var record = await LinkedAsync("921", TimeSpan.FromDays(1));
        var at = _h.Time.GetUtcNow();
        Assert.True(await RefreshObservations.NoteAsync(_db.Db, record, at, default));
        Assert.False(await RefreshObservations.NoteAsync(_db.Db, record, at.AddDays(1), default)); // nothing changed
        for (var i = 1; i <= 30; i++)
        {
            record.LatestChapter = i;
            await RefreshObservations.NoteAsync(_db.Db, record, at.AddDays(i), default);
        }
        var rows = await _db.Db.MetadataRecordObservations.Where(o => o.RecordId == record.Id).OrderBy(o => o.ObservedAt).ToListAsync();
        Assert.Equal(RefreshObservations.MaxPerRecord, rows.Count);
        Assert.Equal(30, rows[^1].LatestChapter);
        Assert.Equal(7, rows[0].LatestChapter);
    }

    [Fact]
    public async Task AShorterCadence_PullsInTheScheduledCompanionChecks_AndCompanionScheduleFollowsIt()
    {
        var year = _h.Time.GetUtcNow().Year - 10;
        var record = await LinkedAsync("931", TimeSpan.FromDays(1), year, 70);
        var now = _h.Time.GetUtcNow();
        _db.Db.MetadataCompanions.Add(new MetadataCompanionEntity
        {
            RecordId = record.Id,
            Provider = "mangadex",
            State = 1,
            CheckedAt = now.AddDays(-1),
            NextCheckAt = now.AddDays(29),
        });
        await _db.Db.SaveChangesAsync();
        await _h.EnableAutomaticAsync(); // linked = in a library whose "Fetch from the web" is on

        var cadences = await _h.Refresh().RecomputeCadencesAsync();

        Assert.Equal(7, cadences[record.Id]);
        _db.Db.ChangeTracker.Clear();
        Assert.Equal(now.AddDays(6), (await _db.Db.MetadataCompanions.SingleAsync()).NextCheckAt);
        var stored = await ReloadAsync(record.Id);
        Assert.Equal(now.AddDays(7), CompanionSchedule.NextCheck(stored, now));
    }

    [Fact]
    public async Task RefreshSummary_CountsOverdueSeries_AndEachCadence()
    {
        var year = _h.Time.GetUtcNow().Year - 10;
        await LinkedAsync("941", TimeSpan.FromDays(8), year, 70);
        await LinkedAsync("942", TimeSpan.FromDays(8), year, 20);
        await LinkedAsync("943", TimeSpan.FromDays(8), status: MetadataOriginStatus.Complete);
        await _h.EnableAutomaticAsync();
        var refresh = _h.Refresh();
        await refresh.RecomputeCadencesAsync();

        var (overdue, byDays) = await refresh.SummaryAsync();

        Assert.Equal(1, overdue);
        Assert.Equal(new Dictionary<int, int> { [7] = 1, [30] = 1, [90] = 1 }, byDays.OrderBy(p => p.Key).ToDictionary());
    }
}
