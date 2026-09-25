namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests (1.24.0) for the settings service (both toggles, consent,
/// budget), the ComicInfo persister (per content version, every outcome stored),
/// and the backfill's selection + throttled pass core.
/// </summary>
public sealed class MetadataSettingsAndComicInfoTests
{
    [Fact]
    public async Task Settings_Defaults_ShowOn_FetchOff_Budget5000()
    {
        await using var t = await MetadataTestDb.CreateAsync();

        var s = await t.Settings().GetAsync();

        Assert.True(s.ShowSeriesInfo);
        Assert.False(s.FetchEnabled);
        Assert.False(s.NetworkDisabledByConfig);
        Assert.Equal(5000, s.DailyBudget);
        Assert.Equal(5000, s.DefaultDailyBudget);
        Assert.Equal(MetadataConsent.CurrentVersion, s.CurrentConsentVersion);
        Assert.Null(s.AcceptedConsentVersion);
        Assert.Equal(0, s.BudgetUsedToday);
        Assert.Single(s.Libraries);
        Assert.True(s.Libraries[0].ShowSeriesInfo);
        Assert.False(s.Libraries[0].FetchEnabled);
    }

    [Fact]
    public async Task Settings_NetworkDisabledByConfig_IsReported()
    {
        await using var t = await MetadataTestDb.CreateAsync();

        var s = await t.Settings(new Dictionary<string, string?> { ["Metadata:NetworkDisabled"] = "true" }).GetAsync();

        Assert.True(s.NetworkDisabledByConfig);
    }

    [Fact]
    public async Task EnablingFetch_RequiresTheCurrentConsentVersion()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        await t.AddUserAsync("admin", isAdmin: true);
        var settings = t.Settings();

        Assert.Equal("consent_required", await settings.UpdateAsync(new UpdateMetadataSettingsRequest { FetchEnabled = true }, "admin"));
        Assert.Equal("consent_required", await settings.UpdateAsync(new UpdateMetadataSettingsRequest { FetchEnabled = true, AcceptedConsentVersion = 0 }, "admin"));
        Assert.False((await settings.GetAsync()).FetchEnabled);

        Assert.Null(await settings.UpdateAsync(new UpdateMetadataSettingsRequest { FetchEnabled = true, AcceptedConsentVersion = MetadataConsent.CurrentVersion }, "admin"));
        var s = await settings.GetAsync();
        Assert.True(s.FetchEnabled);
        Assert.Equal(MetadataConsent.CurrentVersion, s.AcceptedConsentVersion);
        Assert.NotNull(s.ConsentAt);

        var audit = await t.Db.AuditEvents.SingleAsync(a => a.Action == AuditActions.MetadataSettingsEnable);
        Assert.Equal("consent_v1", audit.Result);
    }

    [Fact]
    public async Task DailyBudget_ValidatesAndResets()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var settings = t.Settings();

        Assert.Equal("invalid_daily_budget", await settings.UpdateAsync(new UpdateMetadataSettingsRequest { DailyBudget = 0 }, "admin"));
        Assert.Equal("invalid_daily_budget", await settings.UpdateAsync(new UpdateMetadataSettingsRequest { DailyBudget = -5 }, "admin"));
        Assert.Null(await settings.UpdateAsync(new UpdateMetadataSettingsRequest { DailyBudget = 12345 }, "admin"));
        Assert.Equal(12345, (await settings.GetAsync()).DailyBudget);
        Assert.Null(await settings.UpdateAsync(new UpdateMetadataSettingsRequest { ResetDailyBudget = true }, "admin"));
        Assert.Equal(5000, (await settings.GetAsync()).DailyBudget);
    }

    [Fact]
    public async Task LibraryToggles_PersistAndAuditTheLibrary()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var settings = t.Settings();

        Assert.True(await settings.UpdateLibraryAsync(t.LibraryPublicId, new UpdateMetadataLibraryRequest { FetchEnabled = true, ShowSeriesInfo = false }, "admin"));
        Assert.False(await settings.UpdateLibraryAsync("nope", new UpdateMetadataLibraryRequest { FetchEnabled = true }, "admin"));

        var lib = (await settings.GetAsync()).Libraries.Single();
        Assert.True(lib.FetchEnabled);
        Assert.False(lib.ShowSeriesInfo);
        Assert.True(await settings.IsSeriesInfoHiddenAsync(t.LibraryId));
        var actions = await t.Db.AuditEvents.Where(a => a.TargetLibraryId == t.LibraryId).Select(a => a.Action).ToListAsync();
        Assert.Contains(AuditActions.MetadataLibraryEnable, actions);
        Assert.Contains(AuditActions.MetadataLibraryShowDisable, actions);
    }

    [Fact]
    public async Task ComicInfoPersister_OverwritesAStaleRow_AndStoresAbsent()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var archive = await t.AddArchiveAsync(null, "v1", contentVersion: 2);
        await t.AddComicInfoAsync(archive, "Old", contentVersion: 1, genres: ["G"]);

        await ComicInfoPersister.StageAsync(t.Db, archive.Id, 2, new ComicInfoOutcome { Status = ComicInfoStatus.Absent }, DateTimeOffset.UtcNow);
        await t.Db.SaveChangesAsync();

        var row = await t.Db.EmbeddedMetadata.SingleAsync();
        Assert.Equal(0, row.State);
        Assert.Equal(2, row.ContentVersion);
        Assert.Null(row.Series);
        Assert.Null(row.GenresJson);
    }

    [Fact]
    public void ComicInfoPersister_ParsedWithoutPayload_IsMalformed_AndCapsValues()
    {
        var row = new EmbeddedMetadataEntity();
        ComicInfoPersister.Apply(row, 1, new ComicInfoOutcome { Status = ComicInfoStatus.Parsed }, DateTimeOffset.UtcNow);
        Assert.Equal(2, row.State);

        ComicInfoPersister.Apply(row, 1, new ComicInfoOutcome
        {
            Status = ComicInfoStatus.Parsed,
            Payload = new ComicInfoPayload { Series = new string('x', 2000), Number = new string('9', 100), MangaDirection = 9, WebUrls = ["javascript:alert(1)", "https://ok.example/a"] },
        }, DateTimeOffset.UtcNow);
        Assert.Equal(1, row.State);
        Assert.Equal(512, row.Series!.Length);
        Assert.Equal(32, row.Number!.Length);
        Assert.Null(row.MangaDirection);
        Assert.Equal("[\"https://ok.example/a\"]", row.WebUrlsJson);
    }

    [Fact]
    public async Task Backfill_SelectsReadyArchivesWithoutACurrentRow_InNodeOrder()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var fresh = await t.AddArchiveAsync(null, "fresh");
        var stale = await t.AddArchiveAsync(null, "stale", contentVersion: 3);
        var current = await t.AddArchiveAsync(null, "current");
        var pending = await t.AddArchiveAsync(null, "pending", analysisState: 1);
        var gone = await t.AddArchiveAsync(null, "gone");
        await t.AddComicInfoAsync(stale, "S", contentVersion: 2);
        await t.AddComicInfoAsync(current, "C");
        gone.Availability = 5;
        await t.Db.SaveChangesAsync();

        var batch = await ComicInfoBackfillService.SelectBatchAsync(t.Db, 0, 50, CancellationToken.None);

        Assert.Equal([fresh.Id, stale.Id], batch.Select(c => c.NodeId));
        Assert.Equal(3, batch[1].ContentVersion);
        Assert.Empty(await ComicInfoBackfillService.SelectBatchAsync(t.Db, stale.Id, 50, CancellationToken.None));
        _ = pending;

        var (total, read, found) = await ComicInfoBackfillService.CountAsync(t.Db, CancellationToken.None);
        Assert.Equal(3, total);  // fresh, stale, current (pending and tombstoned excluded)
        Assert.Equal(1, read);   // current
        Assert.Equal(1, found);
    }

    [Fact]
    public async Task BackfillCore_YieldsWhileSaturated_AndAttemptsEachItemOnce()
    {
        var candidates = Enumerable.Range(1, 120)
            .Select(i => new ComicInfoBackfillCandidate(i, 1, 0, 0, "/r", $"a{i}"))
            .ToList();
        var saturatedPolls = 3;
        var attempted = new List<long>();

        var counts = await ComicInfoBackfillService.RunPassCoreAsync(
            (after, limit, _) => Task.FromResult<IReadOnlyList<ComicInfoBackfillCandidate>>(
                candidates.Where(c => c.NodeId > after).Take(limit).ToList()),
            (c, _) =>
            {
                attempted.Add(c.NodeId);
                return Task.FromResult(c.NodeId % 3 == 0 ? ComicInfoItemResult.StoredWithComicInfo
                    : c.NodeId % 3 == 1 ? ComicInfoItemResult.Stored : ComicInfoItemResult.Skipped);
            },
            () => saturatedPolls-- > 0,
            () => 0,
            backoffMs: 1,
            CancellationToken.None);

        Assert.Equal(120, attempted.Count);
        Assert.Equal(120, attempted.Distinct().Count());
        Assert.Equal(120, counts.Attempted);
        Assert.Equal(80, counts.Stored);
        Assert.Equal(40, counts.Found);
        Assert.Equal(40, counts.Skipped);
        Assert.True(saturatedPolls < 0);
    }
}
