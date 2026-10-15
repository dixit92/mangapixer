namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The 1.43.0 tracking redesign through the real pipeline (lane G): the daily cap setting, "Set progress from &lt;tracker&gt; to MangaPixer",
/// the user's dates, the paged / filtered / searched Tracking list with its counts and the attention badge, the series popover, the cards'
/// "read on &lt;tracker&gt;" flag and the E1 catch-up on a changed volume list. FAKE tracker clients only - no request to any real tracker.
/// </summary>
public sealed class TrackingPageHttpTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static async Task<(TrackerTestHarness H, HttpClient Admin, long UserId, TrackerSeriesSeed Series)> MangaUpdatesAsync(int chapters = 10)
    {
        var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        await h.ConnectMangaUpdatesAsync(admin);
        var series = await h.SeedSeriesAsync(chapters);
        return (h, admin, await h.UserIdAsync("admin"), series);
    }

    private static Task<HttpResponseMessage> MarkReadAsync(HttpClient client, string itemPublicId) =>
        client.PutAsync($"/api/v1/reading/{itemPublicId}/read", null);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    // --- E5: the daily cap is an admin setting ---

    [Fact]
    public async Task TheDailyCap_IsAnAdminSetting_Default2000_AndHonoured()
    {
        var (h, admin, userId, series) = await MangaUpdatesAsync();
        await using var _ = h;

        Assert.Equal(2000, (await ReadAsync<AdminTrackerSettingsDto>(await admin.GetAsync("/api/v1/admin/trackers/mangaupdates"))).DailyRequestCap);
        var overview = await ReadAsync<TrackersOverviewDto>(await admin.GetAsync("/api/v1/trackers"));
        Assert.Equal(2000, overview.Trackers.Single(t => t.Slug == TrackerSlugs.MangaUpdates).Account!.DailyRequestCap);
        foreach (var bad in new[] { 99, 20_001 })
        {
            var refused = await admin.PutAsJsonAsync("/api/v1/admin/trackers/mangaupdates", new UpdateTrackerSettingsRequest { DailyRequestCap = bad });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("invalid_daily_cap", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        var set = await admin.PutAsJsonAsync("/api/v1/admin/trackers/mangaupdates", new UpdateTrackerSettingsRequest { DailyRequestCap = 150 });
        Assert.Equal(150, (await ReadAsync<AdminTrackerSettingsDto>(set)).DailyRequestCap);

        // 150 requests used today: the next read mark makes no request and waits.
        await h.DbAsync(async db =>
        {
            var account = await db.TrackerAccounts.SingleAsync(a => a.UserId == userId && a.Tracker == TrackerSlugs.MangaUpdates);
            account.RequestsDay = TrackerAccountService.DayOf(DateTimeOffset.UtcNow);
            account.RequestsUsed = 150;
            return await db.SaveChangesAsync();
        });
        await MarkReadAsync(admin, series.Archives[0].PublicId);
        await h.IdleAsync();
        Assert.Equal(0, h.MangaUpdates.CallCount);
        Assert.Equal("daily_cap", (await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, series.RecordId))!.ErrorCode);

        // The admin raises it: the user's next read goes out.
        await admin.PutAsJsonAsync("/api/v1/admin/trackers/mangaupdates", new UpdateTrackerSettingsRequest { DailyRequestCap = 300 });
        await MarkReadAsync(admin, series.Archives[1].PublicId);
        await h.IdleAsync();
        Assert.Equal(2, Assert.Single(h.MangaUpdates.Writes).Write.ChaptersRead);
    }

    // --- "Set progress from <tracker> to MangaPixer" ---

    [Fact]
    public async Task FromTracker_MarksTheArchivesTheTrackerCovers_NeverUnmarks_AndSendsNothing()
    {
        var (h, admin, userId, series) = await MangaUpdatesAsync();
        await using var _ = h;
        // Chapter 9 read here and sent; then the user's list on the tracker says 6 (they changed it there).
        await MarkReadAsync(admin, series.Archives[8].PublicId);
        await h.IdleAsync();
        h.MangaUpdates.Entries[series.MangaUpdatesId] = new TrackerEntry(TrackerEntryStatus.Reading, 6, null, null, null, false, null, null);
        var writes = h.MangaUpdates.Writes.Count;
        var reads = h.MangaUpdates.Reads.Count;

        var result = await ReadAsync<TrackerFromTrackerResultDto>(
            await admin.PostAsync($"/api/v1/trackers/mangaupdates/entries/{series.RecordPublicId}/from-tracker", null));
        await h.IdleAsync();

        Assert.Equal((6, 0, 6), (result.Marked, result.AlreadyRead, result.RemoteChapters));
        Assert.Equal(reads + 1, h.MangaUpdates.Reads.Count); // one read of the entry
        Assert.Equal(writes, h.MangaUpdates.Writes.Count); // the new marks send nothing out
        var marks = await h.DbAsync(db => db.ReadMarks.AsNoTracking().Where(m => m.UserId == userId).ToListAsync());
        var byItem = marks.ToDictionary(m => m.ItemId, m => m.Source);
        for (var c = 0; c < 6; c++)
            Assert.Equal("tracker", byItem[series.Archives[c].Id]);
        Assert.False(byItem.ContainsKey(series.Archives[6].Id));
        Assert.Equal("manual", byItem[series.Archives[8].Id]); // never unmarked
        Assert.Equal(9, result.Entry.Chapters);

        // Again: everything it covers is read already.
        var again = await ReadAsync<TrackerFromTrackerResultDto>(
            await admin.PostAsync($"/api/v1/trackers/mangaupdates/entries/{series.RecordPublicId}/from-tracker", null));
        Assert.Equal((0, 6), (again.Marked, again.AlreadyRead));
    }

    [Fact]
    public async Task FromTracker_MarksOnlyInTheUsersLibraries_AndRefusesASeriesNotOnTheirList()
    {
        var (h, admin, _, series) = await MangaUpdatesAsync(chapters: 3);
        await using var _ = h;
        // The same series in a second library the reader may NOT open.
        var hidden = await h.SeedSeriesAsync(3);
        await h.DbAsync(async db =>
        {
            var link = await db.NodeSeriesLinks.SingleAsync(l => l.NodeId == hidden.FolderId);
            link.RecordId = series.RecordId;
            return await db.SaveChangesAsync();
        });
        await h.Factory.CreateUserAsync("reader", "Reader-Pass-12345", isAdmin: false);
        var readerId = await h.UserIdAsync("reader");
        await h.DbAsync(async db =>
        {
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = readerId, LibraryId = series.LibraryId, GrantedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        var reader = await h.Factory.LoginAsync("reader", "Reader-Pass-12345");
        await h.ConnectMangaUpdatesAsync(reader);

        // Not on the reader's list yet: refused, nothing marked.
        var notOnList = await reader.PostAsync($"/api/v1/trackers/mangaupdates/entries/{series.RecordPublicId}/from-tracker", null);
        Assert.Equal(HttpStatusCode.Conflict, notOnList.StatusCode);
        Assert.Contains("not_on_list", await notOnList.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        h.MangaUpdates.Entries[series.MangaUpdatesId] = new TrackerEntry(TrackerEntryStatus.Reading, 3, null, null, null, false, null, null);
        var result = await ReadAsync<TrackerFromTrackerResultDto>(
            await reader.PostAsync($"/api/v1/trackers/mangaupdates/entries/{series.RecordPublicId}/from-tracker", null));

        Assert.Equal(3, result.Marked);
        var marked = await h.DbAsync(db => db.ReadMarks.AsNoTracking().Where(m => m.UserId == readerId).Select(m => m.ItemId).ToListAsync());
        Assert.Equal(series.Archives.Select(a => a.Id).Order(), marked.Order());
        Assert.DoesNotContain(hidden.Archives[0].Id, marked);
    }

    [Fact]
    public async Task ATrackerMark_NeverTriggersASync_ButAManualOneAfterItDoes()
    {
        var (h, admin, userId, series) = await MangaUpdatesAsync();
        await using var _ = h;
        // A "tracker" mark made directly (as the action does): the relay ignores it.
        await h.DbAsync(async db =>
        {
            db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = series.Archives[4].Id, MarkedAt = DateTimeOffset.UtcNow, Source = "tracker" });
            return await db.SaveChangesAsync();
        });
        await h.IdleAsync();
        Assert.Equal(0, h.MangaUpdates.CallCount);

        await MarkReadAsync(admin, series.Archives[0].PublicId);
        await h.IdleAsync();
        Assert.Equal(5, Assert.Single(h.MangaUpdates.Writes).Write.ChaptersRead); // it counts in the numbers like a manual mark
    }

    // --- A1: the user's dates (MyAnimeList) ---

    [Fact]
    public async Task TheUsersStartDate_IsSentOnTheNextWrite_AndClearedIsAutomaticAgain()
    {
        var h = new TrackerTestHarness();
        await using var _ = h;
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MyAnimeList);
        await h.ConnectMalAsync(admin);
        var series = await h.SeedSeriesAsync(5);
        await MarkReadAsync(admin, series.Archives[2].PublicId);
        await h.IdleAsync();
        Assert.Equal(Today, Assert.Single(h.Mal.Writes).Write.StartedOn); // the automatic start: the first counted mark

        var own = new DateOnly(2020, 5, 6);
        var row = await ReadAsync<TrackerSeriesRowDto>(await admin.PutAsJsonAsync(
            $"/api/v1/trackers/mal/entries/{series.RecordPublicId}/dates", new TrackerDatesRequest { StartedOn = own }));
        Assert.Equal((own, true, true), (row.Dates.StartedOn, row.Dates.StartedOnOverridden, row.Dates.Pending));

        // Sync now: nothing new to count, but the date goes out on its own (no numbers, the tracker's status).
        await ReadAsync<TrackerEntryDto>(await admin.PostAsync($"/api/v1/trackers/mal/entries/{series.RecordPublicId}/sync", null));
        var dateWrite = h.Mal.Writes.Last().Write;
        Assert.Equal(new TrackerEntryWrite(TrackerEntryStatus.Reading, null, null, own, null, null), dateWrite);
        var page = await ReadAsync<TrackerSeriesPageDto>(await admin.GetAsync("/api/v1/trackers/mal/series"));
        Assert.False(Assert.Single(page.Items).Dates.Pending);

        // Cleared: the automatic date again (not sent - the tracker has a date).
        var cleared = await ReadAsync<TrackerSeriesRowDto>(await admin.PutAsJsonAsync(
            $"/api/v1/trackers/mal/entries/{series.RecordPublicId}/dates", new TrackerDatesRequest()));
        Assert.Equal((Today, false, false), (cleared.Dates.StartedOn, cleared.Dates.StartedOnOverridden, cleared.Dates.Pending));

        // Refused: a finish before the start, and a tracker that keeps no dates.
        var bad = await admin.PutAsJsonAsync($"/api/v1/trackers/mal/entries/{series.RecordPublicId}/dates",
            new TrackerDatesRequest { StartedOn = own, FinishedOn = own.AddDays(-1) });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var mu = await admin.PutAsJsonAsync($"/api/v1/trackers/mangaupdates/entries/{series.RecordPublicId}/dates", new TrackerDatesRequest { StartedOn = own });
        Assert.Contains("dates_not_supported", await mu.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // --- The Tracking list ---

    [Fact]
    public async Task TheList_PagesBy50_FiltersByChip_SearchesTitles_AndCounts()
    {
        var (h, admin, userId, series) = await MangaUpdatesAsync(chapters: 1);
        await using var _ = h;
        var now = DateTimeOffset.UtcNow;
        await h.DbAsync(async db =>
        {
            for (var i = 1; i <= 60; i++)
            {
                var record = new MetadataRecordEntity
                {
                    PublicId = $"lst-{i:000}", Provider = "mangaupdates", ExternalId = (50000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Title = $"Listed Series {i:000}", FetchedAt = now,
                };
                db.MetadataRecords.Add(record);
                await db.SaveChangesAsync();
                var state = i <= 3 ? TrackerEntryState.Held : i <= 5 ? TrackerEntryState.TrackerAhead : i == 6 ? TrackerEntryState.NoId : TrackerEntryState.Ok;
                db.TrackerEntries.Add(new TrackerEntryEntity
                {
                    UserId = userId, Tracker = TrackerSlugs.MangaUpdates, RecordId = record.Id, State = (int)state,
                    RemoteId = state == TrackerEntryState.NoId ? null : record.ExternalId,
                    LocalChapters = 4, RemoteChapters = state == TrackerEntryState.TrackerAhead ? 9 : 4,
                    HeldChapters = state == TrackerEntryState.Held ? 8 : null, RemoteSeenAt = now, UpdatedAt = now,
                });
            }
            return await db.SaveChangesAsync();
        });

        var first = await ReadAsync<TrackerSeriesPageDto>(await admin.GetAsync("/api/v1/trackers/mangaupdates/series"));
        Assert.Equal((1, 50, 60, 50), (first.Page, first.PageSize, first.TotalCount, first.Items.Count));
        Assert.Equal("Listed Series 001", first.Items[0].Title);
        Assert.Equal((60, 3, 2, 1, 54, 5), (first.Counts.All, first.Counts.Held, first.Counts.TrackerAhead, first.Counts.NoId, first.Counts.InSync,
            first.Counts.Attention));
        var second = await ReadAsync<TrackerSeriesPageDto>(await admin.GetAsync("/api/v1/trackers/mangaupdates/series?page=2"));
        Assert.Equal(10, second.Items.Count);

        var held = await ReadAsync<TrackerSeriesPageDto>(await admin.GetAsync("/api/v1/trackers/mangaupdates/series?filter=Held"));
        Assert.Equal(3, held.TotalCount);
        Assert.All(held.Items, r => Assert.Equal((TrackerListBucket.Held, true, 8), (r.Bucket, r.ToTracker.Possible, r.AllChapters)));
        var ahead = await ReadAsync<TrackerSeriesPageDto>(await admin.GetAsync("/api/v1/trackers/mangaupdates/series?filter=trackerahead"));
        Assert.All(ahead.Items, r => Assert.Equal((true, TrackerActionBlock.NotAhead), (r.FromTracker.Possible, r.ToTracker.Block)));
        Assert.Equal(5, (await ReadAsync<TrackerSeriesPageDto>(await admin.GetAsync("/api/v1/trackers/mangaupdates/series?filter=Attention"))).TotalCount);

        var search = await ReadAsync<TrackerSeriesPageDto>(await admin.GetAsync("/api/v1/trackers/mangaupdates/series?q=series%2005"));
        Assert.Equal(10, search.TotalCount);
        Assert.Equal(60, search.Counts.All); // the chips count the whole list

        foreach (var bad in new[] { "?filter=Nope", "?filter=7", "?page=0", "?q=" + new string('x', 201) })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/trackers/mangaupdates/series" + bad)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/trackers/nope/series")).StatusCode);

        // The badge: held + tracker ahead.
        var attention = await ReadAsync<TrackerAttentionDto>(await admin.GetAsync("/api/v1/trackers/attention"));
        Assert.Equal(5, attention.Total);
        Assert.Equal(5, Assert.Single(attention.Trackers).Count);

        // Another user sees none of it.
        await h.Factory.CreateUserAsync("other", "Other-Pass-12345", isAdmin: false);
        var other = await h.Factory.LoginAsync("other", "Other-Pass-12345");
        Assert.Equal(0, (await ReadAsync<TrackerSeriesPageDto>(await other.GetAsync("/api/v1/trackers/mangaupdates/series"))).TotalCount);
        Assert.Equal(0, (await ReadAsync<TrackerAttentionDto>(await other.GetAsync("/api/v1/trackers/attention"))).Total);
    }

    [Fact]
    public async Task ThePreview_ListsEverySeriesWithAMark_AlsoBeyondTheBatchItReads()
    {
        var (h, admin, userId, first) = await MangaUpdatesAsync(chapters: 1);
        await using var _ = h;
        // 52 series with a mark from before the account was usable (no entry): the preview reads 50 of them per call.
        var series = new List<TrackerSeriesSeed> { first };
        for (var i = 1; i < TrackerFacts.PreviewBatch + 2; i++)
            series.Add(await h.SeedSeriesAsync(1));
        await h.DbAsync(async db =>
        {
            foreach (var s in series)
                db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = s.Archives[0].Id, MarkedAt = DateTimeOffset.UtcNow, Source = "import" });
            return await db.SaveChangesAsync();
        });
        await h.IdleAsync();
        await h.DbAsync(db => db.TrackerEntries.Where(e => e.UserId == userId).ExecuteDeleteAsync());
        Assert.Equal(0, (await ReadAsync<TrackerSeriesPageDto>(await admin.GetAsync("/api/v1/trackers/mangaupdates/series"))).TotalCount);

        var preview = await ReadAsync<TrackerPreviewDto>(await admin.PostAsync("/api/v1/trackers/mangaupdates/preview", null));
        Assert.Equal(2, preview.Remaining);

        var page = await ReadAsync<TrackerSeriesPageDto>(await admin.GetAsync("/api/v1/trackers/mangaupdates/series"));
        Assert.Equal(series.Count, page.TotalCount);
        Assert.Equal(2, page.Counts.Unchecked);
    }

    // --- The series popover ---

    [Fact]
    public async Task TheSummary_HasARowPerConnectedTracker_WithoutARequest()
    {
        var (h, admin, userId, series) = await MangaUpdatesAsync();
        await using var _ = h;
        await h.DbAsync(async db =>
        {
            db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = series.Archives[3].Id, MarkedAt = DateTimeOffset.UtcNow, Source = "bulk" });
            return await db.SaveChangesAsync();
        });
        await h.IdleAsync();
        await h.DbAsync(db => db.TrackerEntries.Where(e => e.UserId == userId).ExecuteDeleteAsync());
        var calls = h.MangaUpdates.CallCount;

        var summary = await ReadAsync<TrackerSeriesSummaryDto>(await admin.GetAsync($"/api/v1/trackers/nodes/{series.FolderPublicId}/summary"));

        Assert.Equal(series.RecordPublicId, summary.SeriesId);
        var row = Assert.Single(summary.Rows);
        Assert.Equal((TrackerSlugs.MangaUpdates, TrackerListBucket.Unchecked, (int?)null, (int?)4, series.MangaUpdatesId),
            (row.Tracker, row.Bucket, row.Chapters, row.AllChapters, row.RemoteId));
        Assert.Equal(calls, h.MangaUpdates.CallCount);
        Assert.Null(await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, series.RecordId)); // nothing stored

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/trackers/nodes/no-such-node/summary")).StatusCode);
        // An archive of the series answers for the series too.
        Assert.Equal(series.RecordPublicId,
            (await ReadAsync<TrackerSeriesSummaryDto>(await admin.GetAsync($"/api/v1/trackers/nodes/{series.Archives[0].PublicId}/summary"))).SeriesId);
    }

    // --- The cards' "read on <tracker>" ---

    [Fact]
    public async Task Cards_AreReadOnTheTracker_UpToItsNumbers_OnlyForATrackedLinkedSeries()
    {
        var (h, admin, _, series) = await MangaUpdatesAsync(chapters: 6);
        await using var _ = h;
        h.MangaUpdates.Entries[series.MangaUpdatesId] = new TrackerEntry(TrackerEntryStatus.Reading, 4, null, null, null, false, null, null);
        await MarkReadAsync(admin, series.Archives[0].PublicId);
        await h.IdleAsync();

        var cards = await BrowseAsync(admin, series.LibraryPublicId, series.FolderPublicId);
        Assert.Equal(6, cards.Count);
        foreach (var card in cards)
        {
            var chapter = int.Parse(card.GetProperty("displayName").GetString()!["Chapter ".Length..], System.Globalization.CultureInfo.InvariantCulture);
            if (chapter <= 4)
                Assert.Equal(["mangaupdates"], card.GetProperty("trackerRead").EnumerateArray().Select(e => e.GetString()!).ToArray());
            else
                Assert.False(card.TryGetProperty("trackerRead", out var __)); // left out, not null
        }

        // "Don't sync" hides it; an untracked series never has it.
        await admin.PutAsJsonAsync($"/api/v1/trackers/mangaupdates/entries/{series.RecordPublicId}/dont-sync", new TrackerDontSyncRequest { DontSync = true });
        Assert.All(await BrowseAsync(admin, series.LibraryPublicId, series.FolderPublicId), c => Assert.False(c.TryGetProperty("trackerRead", out var __)));
        var untracked = await h.SeedSeriesAsync(2);
        Assert.All(await BrowseAsync(admin, untracked.LibraryPublicId, untracked.FolderPublicId), c => Assert.False(c.TryGetProperty("trackerRead", out var __)));
    }

    private static async Task<List<JsonElement>> BrowseAsync(HttpClient client, string libraryId, string folderId)
    {
        var response = await client.GetAsync($"/api/v1/libraries/{libraryId}/browse?parentId={folderId}&pageSize=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    // --- E1 catch-up: a changed volume list re-queues the series ---

    [Fact]
    public async Task AChangedVolumeList_SyncsTheSeriesAgain_AnUnchangedOneSendsNothing()
    {
        var (h, admin, userId, series) = await MangaUpdatesAsync(chapters: 8);
        await using var _ = h;
        for (var c = 0; c < 4; c++)
            await MarkReadAsync(admin, series.Archives[c].PublicId);
        await h.IdleAsync();
        Assert.Equal((4, (int?)null), (h.MangaUpdates.Writes.Last().Write.ChaptersRead, h.MangaUpdates.Writes.Last().Write.VolumesRead));
        var reads = h.MangaUpdates.Reads.Count;
        var writes = h.MangaUpdates.Writes.Count;

        async Task StoreListAsync()
        {
            using var scope = h.Factory.Services.CreateScope();
            var maps = scope.ServiceProvider.GetRequiredService<VolumeMapService>();
            var db = scope.ServiceProvider.GetRequiredService<com.lifepixer.mangapixer.Server.Persistence.MangaPixerDbContext>();
            var record = await db.MetadataRecords.SingleAsync(r => r.Id == series.RecordId);
            await maps.StoreAsync(record, VolumeMapSource.MangaDexAggregate, VolumeMapState.Ok,
                """[{"v":"1","c":["1","2","3","4"]},{"v":"2","c":["5","6","7","8"]}]""", null, 4.0, 2, CancellationToken.None);
        }

        // The list arrives: chapters 1-4 make volume 1 (E1) - the tracker is read and gets volume 1.
        await StoreListAsync();
        await h.IdleAsync();
        Assert.Equal(reads + 1, h.MangaUpdates.Reads.Count);
        Assert.Equal(writes + 1, h.MangaUpdates.Writes.Count);
        Assert.Equal((4, (int?)1), (h.MangaUpdates.Writes.Last().Write.ChaptersRead, h.MangaUpdates.Writes.Last().Write.VolumesRead));
        Assert.Equal(1, (await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, series.RecordId))!.LocalVolumes);

        // The same answer again: unchanged - no request.
        await StoreListAsync();
        await h.IdleAsync();
        Assert.Equal(reads + 1, h.MangaUpdates.Reads.Count);
        Assert.Equal(writes + 1, h.MangaUpdates.Writes.Count);
    }
}
