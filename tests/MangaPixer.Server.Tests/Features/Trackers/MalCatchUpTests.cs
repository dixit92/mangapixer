namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Owner A2 (1.43.0, lane G): the one-time <c>links.mal</c> catch-up. Inside a refresh pass that passed the automatic gate (the consent that
/// already allows background MangaDex refreshes), every linked MangaDex record without a MAL id is refreshed once by its id - within the
/// daily budget - and the MangaDex companions without one become due for their regular re-check; once per instance (a <c>job_runs</c>
/// marker). Service-with-DB over the real gateway and MangaDex provider classes; MangaDex answers are synthetic (no request leaves the test).
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class MalCatchUpTests : IAsyncLifetime
{
    private const string WithoutMal = "6a000000-0000-4000-8000-00000000b001";
    private const string AlreadyMal = "6a000000-0000-4000-8000-00000000b002";
    private const string NeverMal = "6a000000-0000-4000-8000-00000000b003";
    private const string Companion = "6a000000-0000-4000-8000-00000000b004";

    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;
    private readonly Dictionary<string, MdJson.Manga> _md = new(StringComparer.Ordinal);

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new AutoMatchHarness(_db);
        _h.Net.Registry = new MetadataProviderRegistry([_h.Net.Provider, new MangaDexSeriesProvider(new MangaDexProvider(_h.Net.HttpFactory))]);
        var mu = _h.Handler.Respond!;
        _h.Handler.Respond = request => request.RequestUri!.Host == MetadataHttp.MangaDexApiHost ? MangaDex(request) : mu(request);
        _md[WithoutMal] = new MdJson.Manga(WithoutMal, "Synthetic Lantern", Mal: "4242");
        _md[AlreadyMal] = new MdJson.Manga(AlreadyMal, "Synthetic Harbor", Mal: "4343");
        _md[NeverMal] = new MdJson.Manga(NeverMal, "Synthetic Orchard");
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private HttpResponseMessage MangaDex(HttpRequestMessage request)
    {
        var id = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
        return _md.TryGetValue(id, out var manga)
            ? ScriptedHandler.Json(MdJson.Entity(manga))
            : ScriptedHandler.Json("{\"result\":\"error\"}", HttpStatusCode.NotFound);
    }

    private string[] MangaDexPaths() =>
        _h.Handler.Seen.Where(s => s.Uri.Host == MetadataHttp.MangaDexApiHost).Select(s => s.Uri.AbsolutePath).ToArray();

    /// <summary>A folder linked (Confirmed) to a MangaDex series record fetched two days ago (its cadence is not due).</summary>
    private async Task<MetadataRecordEntity> MangaDexSeriesAsync(string mangaId, string? crossIds)
    {
        var folder = await _db.AddFolderAsync(null, "Folder " + mangaId[^4..]);
        var record = new MetadataRecordEntity
        {
            PublicId = "md" + mangaId[^4..],
            Provider = MetadataProviderAllowlist.MangaDex,
            ExternalId = mangaId,
            Title = _md[mangaId].Title,
            CrossIdsJson = crossIds,
            OriginStatus = (int)MetadataOriginStatus.Ongoing,
            FetchedAt = _h.Time.GetUtcNow() - TimeSpan.FromDays(2),
        };
        _db.Db.MetadataRecords.Add(record);
        await _db.Db.SaveChangesAsync();
        await _db.AddLinkAsync(folder, record);
        return record;
    }

    /// <summary>A MangaUpdates-linked series whose MangaDex companion (Auto) has no MAL id and its next re-check in 20 days.</summary>
    private async Task<MetadataCompanionEntity> CompanionWithoutMalAsync()
    {
        var folder = await _db.AddFolderAsync(null, "Folder companion");
        var series = await _db.AddRecordAsync("8801", "Synthetic Meadow");
        series.FetchedAt = _h.Time.GetUtcNow() - TimeSpan.FromDays(2);
        var mangaDex = new MetadataRecordEntity
        {
            PublicId = "mdc-b004",
            Provider = MetadataProviderAllowlist.MangaDex,
            ExternalId = Companion,
            Title = "Synthetic Meadow",
            CrossIdsJson = "{\"mu\":\"8801\"}",
            FetchedAt = _h.Time.GetUtcNow() - TimeSpan.FromDays(2),
        };
        _db.Db.MetadataRecords.Add(mangaDex);
        await _db.Db.SaveChangesAsync();
        await _db.AddLinkAsync(folder, series);
        var companion = new MetadataCompanionEntity
        {
            RecordId = series.Id,
            Provider = MetadataProviderAllowlist.MangaDex,
            CompanionRecordId = mangaDex.Id,
            State = (int)CompanionState.Auto,
            Method = (int)CompanionMethod.CrossLink,
            CheckedAt = _h.Time.GetUtcNow() - TimeSpan.FromDays(2),
            NextCheckAt = _h.Time.GetUtcNow() + TimeSpan.FromDays(20),
        };
        _db.Db.MetadataCompanions.Add(companion);
        await _db.Db.SaveChangesAsync();
        return companion;
    }

    private async Task<T> FreshAsync<T>(Func<Task<T>> read)
    {
        _db.Db.ChangeTracker.Clear();
        return await read();
    }

    [Fact]
    public async Task WithoutTheAutomaticConsent_NothingIsMarkedOrSent()
    {
        await MangaDexSeriesAsync(WithoutMal, null);
        var companion = await CompanionWithoutMalAsync();
        await _h.Net.EnableAsync(); // "Fetch from the web" only - no automatic matching consent

        var result = await _h.Refresh().RunPassAsync();

        Assert.NotNull(result.StoppedCode);
        Assert.Empty(_h.Handler.Seen);
        Assert.False(await FreshAsync(() => _db.Db.JobRuns.AnyAsync(j => j.Key == "tracker-mal-catchup")));
        var after = await FreshAsync(() => _db.Db.MetadataCompanions.AsNoTracking().SingleAsync(c => c.Id == companion.Id));
        Assert.True(after.NextCheckAt > _h.Time.GetUtcNow());
    }

    [Fact]
    public async Task UnderTheGate_EachRecordWithoutAMalIdIsRefreshedOnce_InTheBudget_AndCompanionsBecomeDue()
    {
        var without = await MangaDexSeriesAsync(WithoutMal, null);
        await MangaDexSeriesAsync(AlreadyMal, "{\"mal\":\"4343\"}");
        var never = await MangaDexSeriesAsync(NeverMal, "{}");
        var companion = await CompanionWithoutMalAsync();
        await _h.EnableAutomaticAsync();
        var budgetBefore = (await _h.Net.Budget().GetAsync()).Used;

        var first = await _h.Refresh().RunPassAsync();

        // Only the two records without a MAL id are refreshed (by id); the one that has it and the MangaUpdates record are not due.
        Assert.Equal(2, first.Refreshed);
        Assert.Equal([$"/manga/{WithoutMal}", $"/manga/{NeverMal}"], MangaDexPaths().Order(StringComparer.Ordinal).ToArray());
        // Every request of the catch-up (the record by id, and its poster where it changed) counts in the daily budget.
        Assert.Equal(budgetBefore + _h.Handler.Seen.Count, (await _h.Net.Budget().GetAsync()).Used);
        Assert.Contains("\"mal\":\"4242\"", (await FreshAsync(() => _db.Db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Id == without.Id))).CrossIdsJson);
        var due = (await FreshAsync(() => _db.Db.MetadataCompanions.AsNoTracking().SingleAsync(c => c.Id == companion.Id))).NextCheckAt;
        Assert.True(due <= _h.Time.GetUtcNow());

        // Once per instance: MangaDex lists no MAL id for the third record - it is not asked again, and the catch-up is done.
        _h.Time.Advance(TimeSpan.FromMinutes(5));
        var second = await _h.Refresh().RunPassAsync();

        Assert.Equal(0, second.Refreshed);
        Assert.Equal(2, MangaDexPaths().Length); // no new MangaDex request
        var run = await FreshAsync(() => _db.Db.JobRuns.AsNoTracking().SingleAsync(j => j.Key == "tracker-mal-catchup"));
        Assert.Equal("ok", run.LastOutcome);
        Assert.Null(JsonCross(await FreshAsync(() => _db.Db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Id == never.Id))));
    }

    [Fact]
    public async Task AtStartup_TheCatchUpStartsOnce_UnderTheGate_MakingCompanionsDue_WithoutARequestOfItsOwn()
    {
        // 1.43.1 (owner, after the 1.43.0 deploy): the catch-up starts when the server starts, not at the next nightly refresh.
        var companion = await CompanionWithoutMalAsync();
        await _h.EnableAutomaticAsync();

        var started = await _h.Refresh().StartMalCatchUpAsync(CancellationToken.None);

        Assert.Equal(1, started);
        Assert.Empty(_h.Handler.Seen); // the volume-cover pass re-checks the companion, within the budget
        var due = (await FreshAsync(() => _db.Db.MetadataCompanions.AsNoTracking().SingleAsync(c => c.Id == companion.Id))).NextCheckAt;
        Assert.True(due <= _h.Time.GetUtcNow());
        Assert.True(await FreshAsync(() => _db.Db.JobRuns.AnyAsync(j => j.Key == "tracker-mal-catchup")));
        // Once per instance: a second start (another restart) does nothing.
        Assert.Null(await _h.Refresh().StartMalCatchUpAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AtStartup_WithoutTheAutomaticConsent_TheCatchUpDoesNotStart()
    {
        var companion = await CompanionWithoutMalAsync();
        await _h.Net.EnableAsync();

        Assert.Null(await _h.Refresh().StartMalCatchUpAsync(CancellationToken.None));
        Assert.False(await FreshAsync(() => _db.Db.JobRuns.AnyAsync(j => j.Key == "tracker-mal-catchup")));
        var after = await FreshAsync(() => _db.Db.MetadataCompanions.AsNoTracking().SingleAsync(c => c.Id == companion.Id));
        Assert.True(after.NextCheckAt > _h.Time.GetUtcNow());
    }

    [Fact]
    public async Task ASpentBudget_LeavesTheRestDueForTheNextPass()
    {
        await MangaDexSeriesAsync(WithoutMal, null);
        await MangaDexSeriesAsync(NeverMal, null);
        await _h.EnableAutomaticAsync();
        var settings = await _db.Db.AppSettings.FirstAsync(s => s.Id == AppSettingsEntity.SingletonId);
        settings.MetadataDailyBudget = 1;
        await _db.Db.SaveChangesAsync();

        var first = await _h.Refresh().RunPassAsync();
        Assert.Equal(1, first.Refreshed);
        Assert.NotNull(first.StoppedCode);

        // The next budget day picks up the other one.
        _h.Time.Advance(TimeSpan.FromDays(1));
        var second = await _h.Refresh().RunPassAsync();
        Assert.Equal(1, second.Refreshed);
        Assert.Equal(2, MangaDexPaths().Distinct().Count());
    }

    private static string? JsonCross(MetadataRecordEntity record) =>
        record.CrossIdsJson is { } json && json.Contains("\"mal\"", StringComparison.Ordinal) ? json : null;
}
