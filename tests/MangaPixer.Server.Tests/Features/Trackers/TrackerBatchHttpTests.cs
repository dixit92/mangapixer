namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.43.2 (owner, 2026-10-16): the Tracking list's Select mode through the real pipeline - the batch route (each action exactly as its
/// per-series route; blocked series left as they are and counted by reason; stops at the daily cap; at most 100 ids; only the caller's
/// rows; Send never lowers), the "Select all in this chip" ids route (chip + search + access + cap), and "Send all" gone. FAKE tracker
/// clients only - no request to any real tracker.
/// </summary>
public sealed class TrackerBatchHttpTests
{
    private const string Batch = "/api/v1/trackers/mangaupdates/entries/batch";

    private static async Task<(TrackerTestHarness H, HttpClient Admin, long UserId)> MangaUpdatesAsync()
    {
        var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        await h.ConnectMangaUpdatesAsync(admin);
        return (h, admin, await h.UserIdAsync("admin"));
    }

    private static Task<HttpResponseMessage> BatchAsync(HttpClient client, TrackerBatchAction action, params string[] seriesIds) =>
        client.PostAsJsonAsync(Batch, new TrackerBatchRequest { Action = action, SeriesIds = seriesIds }, TestJson.Web);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    private static int SkipsOf(TrackerBatchResultDto result, TrackerBatchSkip reason) =>
        result.Skips.FirstOrDefault(s => s.Reason == reason)?.Count ?? 0;

    /// <summary>The caller's stored entry for a seeded series (MangaUpdates), as the list would hold it after a read.</summary>
    private static Task EntryAsync(TrackerTestHarness h, long userId, TrackerSeriesSeed series, Action<TrackerEntryEntity> shape) =>
        h.DbAsync(async db =>
        {
            var entry = await db.TrackerEntries.FirstOrDefaultAsync(e => e.UserId == userId && e.Tracker == TrackerSlugs.MangaUpdates
                && e.RecordId == series.RecordId);
            if (entry is null)
            {
                entry = new TrackerEntryEntity { UserId = userId, Tracker = TrackerSlugs.MangaUpdates, RecordId = series.RecordId };
                db.TrackerEntries.Add(entry);
            }
            entry.RemoteId = series.MangaUpdatesId;
            entry.RemoteIdSource = (int)TrackerIdSource.Link;
            entry.State = (int)TrackerEntryState.Ok;
            entry.RemoteSeenAt = DateTimeOffset.UtcNow.AddHours(-1);
            entry.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1);
            shape(entry);
            return await db.SaveChangesAsync();
        });

    private static Task MarkAsync(TrackerTestHarness h, long userId, TrackerSeriesSeed series, int count, string source) =>
        h.DbAsync(async db =>
        {
            foreach (var (id, _) in series.Archives.Take(count))
                db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = id, MarkedAt = DateTimeOffset.UtcNow, Source = source });
            return await db.SaveChangesAsync();
        });

    private static TrackerEntry Remote(int chapters, TrackerEntryStatus status = TrackerEntryStatus.Reading) =>
        new(status, chapters, null, null, null, false, null, null);

    [Fact]
    public async Task FromTracker_MarksEachSeriesItAppliesTo_SkipsTheBlockedWithReasons_AndCountsTheFiles()
    {
        var (h, admin, userId) = await MangaUpdatesAsync();
        await using var _ = h;
        var ahead = await h.SeedSeriesAsync(8);
        var level = await h.SeedSeriesAsync(4);
        var noRow = await h.SeedSeriesAsync(3);
        // MangaUpdates says 6 for "ahead" (2 read here already); "level" is not ahead (3 here, 3 there); "noRow" is not on the list.
        await MarkAsync(h, userId, ahead, 2, "manual");
        await MarkAsync(h, userId, level, 3, "manual");
        await h.IdleAsync();
        h.MangaUpdates.Entries[ahead.MangaUpdatesId] = Remote(6);
        h.MangaUpdates.Entries[level.MangaUpdatesId] = Remote(3);
        await EntryAsync(h, userId, ahead, e => { e.LocalChapters = 2; e.AllChapters = 2; e.RemoteChapters = 6; e.State = (int)TrackerEntryState.TrackerAhead; });
        await EntryAsync(h, userId, level, e => { e.LocalChapters = 3; e.AllChapters = 3; e.RemoteChapters = 3; });
        await h.DbAsync(db => db.TrackerEntries.Where(e => e.RecordId == noRow.RecordId).ExecuteDeleteAsync());
        var reads = h.MangaUpdates.Reads.Count;
        var writes = h.MangaUpdates.Writes.Count;

        var result = await ReadAsync<TrackerBatchResultDto>(
            await BatchAsync(admin, TrackerBatchAction.FromTracker, ahead.RecordPublicId, level.RecordPublicId, noRow.RecordPublicId));
        await h.IdleAsync();

        Assert.Equal((1, 2, 0, false, 0), (result.Done, result.Skipped, result.Failed, result.CapReached, result.NotReached));
        Assert.Equal((4, 2), (result.Marked, result.AlreadyRead));
        Assert.Equal((1, 1), (SkipsOf(result, TrackerBatchSkip.NotAhead), SkipsOf(result, TrackerBatchSkip.NotListed)));
        Assert.Equal(reads + 1, h.MangaUpdates.Reads.Count); // only the series it applies to is read
        Assert.Equal(writes, h.MangaUpdates.Writes.Count); // marks made by it send nothing
        var marked = await h.DbAsync(db => db.ReadMarks.AsNoTracking().Where(m => m.UserId == userId && m.Source == "tracker").Select(m => m.ItemId).ToListAsync());
        Assert.Equal(ahead.Archives.Skip(2).Take(4).Select(a => a.Id).Order(), marked.Order());
        // The rows of the series given (after the action) come back for the ones on the list.
        Assert.Equal(new[] { ahead.RecordPublicId, level.RecordPublicId }.Order(), result.Rows.Select(r => r.SeriesId).Order());
        Assert.Equal(6, result.Rows.Single(r => r.SeriesId == ahead.RecordPublicId).AllChapters);
    }

    [Fact]
    public async Task ToTracker_SendsEveryMark_NeverLowers_AndLeavesTheBlockedAsTheyAre()
    {
        var (h, admin, userId) = await MangaUpdatesAsync();
        await using var _ = h;
        var held = await h.SeedSeriesAsync(6);
        var trackerAhead = await h.SeedSeriesAsync(6);
        var staleAhead = await h.SeedSeriesAsync(6);
        var onHold = await h.SeedSeriesAsync(6);
        // Imported reading (it waits for the user, so the relay sends nothing before the batch).
        await MarkAsync(h, userId, held, 6, "import");
        await MarkAsync(h, userId, trackerAhead, 3, "import");
        await MarkAsync(h, userId, staleAhead, 5, "import");
        await MarkAsync(h, userId, onHold, 4, "import");
        await h.IdleAsync();
        h.MangaUpdates.Entries[held.MangaUpdatesId] = Remote(1);
        h.MangaUpdates.Entries[trackerAhead.MangaUpdatesId] = Remote(9);
        h.MangaUpdates.Entries[staleAhead.MangaUpdatesId] = Remote(8); // the stored row still says 2: Send reads first and never lowers
        h.MangaUpdates.Entries[onHold.MangaUpdatesId] = Remote(1, TrackerEntryStatus.OnHold);
        await EntryAsync(h, userId, held, e => { e.AllChapters = 6; e.HeldChapters = 6; e.RemoteChapters = 1; e.State = (int)TrackerEntryState.Held; });
        await EntryAsync(h, userId, trackerAhead, e => { e.LocalChapters = 3; e.AllChapters = 3; e.RemoteChapters = 9; e.State = (int)TrackerEntryState.TrackerAhead; });
        await EntryAsync(h, userId, staleAhead, e => { e.LocalChapters = 5; e.AllChapters = 5; e.RemoteChapters = 2; });
        await EntryAsync(h, userId, onHold, e => { e.LocalChapters = 4; e.AllChapters = 4; e.RemoteChapters = 1; e.RemoteStatus = (int)TrackerEntryStatus.OnHold; e.State = (int)TrackerEntryState.LeftAsIs; });
        var reads = h.MangaUpdates.Reads.Count;
        var writes = h.MangaUpdates.Writes.Count;

        var result = await ReadAsync<TrackerBatchResultDto>(await BatchAsync(admin, TrackerBatchAction.ToTracker,
            held.RecordPublicId, trackerAhead.RecordPublicId, staleAhead.RecordPublicId, onHold.RecordPublicId));

        Assert.Equal((1, 3, 0, false), (result.Done, result.Skipped, result.Failed, result.CapReached));
        Assert.Equal((2, 1), (SkipsOf(result, TrackerBatchSkip.NotAhead), SkipsOf(result, TrackerBatchSkip.LeftAsIs)));
        Assert.Equal(writes + 1, h.MangaUpdates.Writes.Count);
        var write = h.MangaUpdates.Writes.Last();
        Assert.Equal((held.MangaUpdatesId, 6), (write.MediaId, write.Write.ChaptersRead));
        Assert.Equal(8, h.MangaUpdates.Entries[staleAhead.MangaUpdatesId].ChaptersRead); // never lowered
        Assert.Equal(reads + 2, h.MangaUpdates.Reads.Count); // held + the stale row; the blocked ones are not read
        Assert.Null((await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, held.RecordId))!.HeldChapters);
    }

    [Fact]
    public async Task TheBatch_StopsAtTheDailyCap_AndSaysHowManyItDidNotReach()
    {
        var (h, admin, userId) = await MangaUpdatesAsync();
        await using var _ = h;
        var seeds = new List<TrackerSeriesSeed>();
        for (var i = 0; i < 3; i++)
        {
            var s = await h.SeedSeriesAsync(4);
            await MarkAsync(h, userId, s, 4, "import");
            seeds.Add(s);
        }
        await h.IdleAsync();
        foreach (var s in seeds)
        {
            h.MangaUpdates.Entries[s.MangaUpdatesId] = Remote(1);
            await EntryAsync(h, userId, s, e => { e.AllChapters = 4; e.HeldChapters = 4; e.RemoteChapters = 1; e.State = (int)TrackerEntryState.Held; });
        }
        // Two requests left today: the first series reads + writes, the second meets the cap.
        await h.DbAsync(async db =>
        {
            var account = await db.TrackerAccounts.SingleAsync(a => a.UserId == userId && a.Tracker == TrackerSlugs.MangaUpdates);
            account.RequestsDay = TrackerAccountService.DayOf(DateTimeOffset.UtcNow);
            account.RequestsUsed = TrackerFacts.DefaultDailyRequestCap - 2;
            return await db.SaveChangesAsync();
        });

        var writes = h.MangaUpdates.Writes.Count;

        var result = await ReadAsync<TrackerBatchResultDto>(
            await BatchAsync(admin, TrackerBatchAction.ToTracker, seeds.Select(s => s.RecordPublicId).ToArray()));

        Assert.Equal((1, true, 1), (result.Done, result.CapReached, result.NotReached));
        Assert.Equal(writes + 1, h.MangaUpdates.Writes.Count);
        // The series that met the cap waits ("daily_cap"); the third was never touched.
        Assert.Equal("daily_cap", (await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, seeds[1].RecordId))!.ErrorCode);
        Assert.Equal(4, (await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, seeds[2].RecordId))!.HeldChapters);
    }

    [Fact]
    public async Task DontSync_SyncAgain_AndSyncNow_EachAsTheirPerSeriesRoute()
    {
        var (h, admin, userId) = await MangaUpdatesAsync();
        await using var _ = h;
        var a = await h.SeedSeriesAsync(3);
        var b = await h.SeedSeriesAsync(3);
        await MarkAsync(h, userId, a, 2, "manual");
        await MarkAsync(h, userId, b, 1, "manual");
        await h.IdleAsync();
        h.MangaUpdates.Entries[a.MangaUpdatesId] = Remote(2);
        h.MangaUpdates.Entries[b.MangaUpdatesId] = Remote(1);
        await EntryAsync(h, userId, a, e => { e.LocalChapters = 2; e.AllChapters = 2; e.RemoteChapters = 2; });
        await EntryAsync(h, userId, b, e => { e.LocalChapters = 1; e.AllChapters = 1; e.RemoteChapters = 1; });
        var calls = h.MangaUpdates.CallCount;

        var off = await ReadAsync<TrackerBatchResultDto>(await BatchAsync(admin, TrackerBatchAction.DontSync, a.RecordPublicId, b.RecordPublicId));
        Assert.Equal((2, 0), (off.Done, off.Skipped));
        Assert.All(off.Rows, r => Assert.Equal((true, TrackerListBucket.NotSynced), (r.DontSync, r.Bucket)));
        Assert.Equal(calls, h.MangaUpdates.CallCount); // a switch makes no request

        var again = await ReadAsync<TrackerBatchResultDto>(await BatchAsync(admin, TrackerBatchAction.DontSync, a.RecordPublicId));
        Assert.Equal((0, 1), (again.Done, SkipsOf(again, TrackerBatchSkip.AlreadyOff)));

        var sync = await ReadAsync<TrackerBatchResultDto>(await BatchAsync(admin, TrackerBatchAction.SyncNow, a.RecordPublicId, b.RecordPublicId));
        Assert.Equal((0, 2), (sync.Done, SkipsOf(sync, TrackerBatchSkip.DontSync)));
        Assert.Equal(calls, h.MangaUpdates.CallCount);

        var on = await ReadAsync<TrackerBatchResultDto>(await BatchAsync(admin, TrackerBatchAction.SyncAgain, a.RecordPublicId));
        Assert.Equal(1, on.Done);
        Assert.False(Assert.Single(on.Rows).DontSync);
        var on2 = await ReadAsync<TrackerBatchResultDto>(await BatchAsync(admin, TrackerBatchAction.SyncAgain, a.RecordPublicId));
        Assert.Equal(1, SkipsOf(on2, TrackerBatchSkip.AlreadyOn));

        var now = await ReadAsync<TrackerBatchResultDto>(await BatchAsync(admin, TrackerBatchAction.SyncNow, a.RecordPublicId, b.RecordPublicId));
        Assert.Equal((1, 1), (now.Done, SkipsOf(now, TrackerBatchSkip.DontSync)));
        Assert.Equal(calls + 1, h.MangaUpdates.CallCount); // one read of "a"; in sync, nothing written
    }

    [Fact]
    public async Task TheBatch_RefusesBadRequests_AndActsOnlyOnTheCallersOwnRows()
    {
        var (h, admin, userId) = await MangaUpdatesAsync();
        await using var _ = h;
        var mine = await h.SeedSeriesAsync(3);
        await EntryAsync(h, userId, mine, e => e.LocalChapters = 1);

        var tooMany = Enumerable.Range(0, TrackerFacts.MaxBatchSeries + 1).Select(i => $"s{i}").ToArray();
        foreach (var bad in new[] { await BatchAsync(admin, TrackerBatchAction.SyncNow, tooMany), await BatchAsync(admin, TrackerBatchAction.SyncNow) })
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("invalid_series", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        var unknownAction = await admin.PostAsync(Batch, JsonContent.Create(new { action = "Delete", seriesIds = new[] { mine.RecordPublicId } }));
        Assert.Equal(HttpStatusCode.BadRequest, unknownAction.StatusCode);
        var outOfRange = await admin.PostAsync(Batch, JsonContent.Create(new { action = 99, seriesIds = new[] { mine.RecordPublicId } }));
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
        var unknownTracker = await admin.PostAsJsonAsync("/api/v1/trackers/nope/entries/batch",
            new TrackerBatchRequest { Action = TrackerBatchAction.DontSync, SeriesIds = [mine.RecordPublicId] }, TestJson.Web);
        Assert.Equal(HttpStatusCode.NotFound, unknownTracker.StatusCode);

        // Another user with access to the library but no account: a tracker action is refused as for one series; a switch on the
        // admin's series touches nothing of the admin's (it is not on the other user's list).
        await h.Factory.CreateUserAsync("reader", "Reader-Pass-12345", isAdmin: false);
        var readerId = await h.UserIdAsync("reader");
        await h.DbAsync(async db =>
        {
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = readerId, LibraryId = mine.LibraryId, GrantedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        var reader = await h.Factory.LoginAsync("reader", "Reader-Pass-12345");
        var refused = await BatchAsync(reader, TrackerBatchAction.ToTracker, mine.RecordPublicId);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Contains("not_connected", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var theirs = await ReadAsync<TrackerBatchResultDto>(await BatchAsync(reader, TrackerBatchAction.DontSync, mine.RecordPublicId));
        Assert.Equal((0, 1), (theirs.Done, SkipsOf(theirs, TrackerBatchSkip.NotListed)));
        Assert.Empty(theirs.Rows);
        Assert.False((await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, mine.RecordId))!.DontSync);
        Assert.Null(await h.EntryAsync(readerId, TrackerSlugs.MangaUpdates, mine.RecordId));

        // A series in a library the caller can no longer open: skipped, even with a stored row.
        var hidden = await h.SeedSeriesAsync(2);
        await EntryAsync(h, userId, hidden, e => e.LocalChapters = 1);
        await h.DbAsync(db => db.NodeSeriesLinks.Where(l => l.RecordId == hidden.RecordId).ExecuteDeleteAsync());
        var gone = await ReadAsync<TrackerBatchResultDto>(await BatchAsync(admin, TrackerBatchAction.DontSync, hidden.RecordPublicId));
        Assert.Equal(1, SkipsOf(gone, TrackerBatchSkip.NotListed));
    }

    [Fact]
    public async Task SendAll_IsGone()
    {
        var (h, admin, _) = await MangaUpdatesAsync();
        await using var _ = h;
        var response = await admin.PostAsync("/api/v1/trackers/mangaupdates/entries/send-all", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheIds_FollowTheChipAndTheSearch_OnlyOpenableSeries_WithWhatApplies_AndTheCap()
    {
        var (h, admin, userId) = await MangaUpdatesAsync();
        await using var _ = h;
        var library = (await h.SeedSeriesAsync(1)).LibraryId;
        var total = TrackerFacts.MaxSelectableSeries + 5;
        var now = DateTimeOffset.UtcNow;
        await h.DbAsync(async db =>
        {
            var records = new List<MetadataRecordEntity>();
            var folders = new List<CatalogNodeEntity>();
            for (var i = 1; i <= total; i++)
            {
                var title = i <= 3 ? $"Selectable Held {i:0000}" : $"Selectable Series {i:0000}";
                records.Add(new MetadataRecordEntity
                {
                    PublicId = $"sel-{i:0000}",
                    Provider = "mangaupdates",
                    ExternalId = (60000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Title = title,
                    FetchedAt = now,
                });
                folders.Add(new CatalogNodeEntity
                {
                    PublicId = $"sel-node-{i:0000}",
                    LibraryId = library,
                    Kind = (int)CatalogNodeKind.Folder,
                    DisplayName = title,
                    RelativePath = $"sel-{i:0000}",
                    PathKey = $"sel-{i:0000}",
                    SortKey = SortKey.ForNode(CatalogNodeKind.Folder, title),
                    Availability = (int)CatalogNodeAvailability.Available,
                    CreatedAt = now,
                });
            }
            db.MetadataRecords.AddRange(records);
            db.CatalogNodes.AddRange(folders);
            await db.SaveChangesAsync();
            for (var i = 0; i < total; i++)
            {
                db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
                {
                    NodeId = folders[i].Id,
                    LibraryId = library,
                    State = (int)SeriesLinkState.Confirmed,
                    RecordId = records[i].Id,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                var isHeld = i < 3;
                db.TrackerEntries.Add(new TrackerEntryEntity
                {
                    UserId = userId,
                    Tracker = TrackerSlugs.MangaUpdates,
                    RecordId = records[i].Id,
                    State = (int)(isHeld ? TrackerEntryState.Held : TrackerEntryState.Ok),
                    RemoteId = i == 4 ? null : records[i].ExternalId,
                    LocalChapters = 4,
                    AllChapters = isHeld ? 8 : 4,
                    HeldChapters = isHeld ? 8 : null,
                    RemoteChapters = 4,
                    DontSync = i == 5,
                    RemoteSeenAt = now,
                    UpdatedAt = now,
                });
            }
            return await db.SaveChangesAsync();
        });

        var held = await ReadAsync<TrackerSelectableSeriesListDto>(await admin.GetAsync("/api/v1/trackers/mangaupdates/series/ids?filter=Held"));
        Assert.Equal((3, false), (held.TotalCount, held.Truncated));
        Assert.Equal(new[] { "sel-0001", "sel-0002", "sel-0003" }, held.Items.Select(i => i.SeriesId));
        Assert.All(held.Items, i => Assert.Equal((true, false, true, false), (i.ToTracker.Possible, i.FromTracker.Possible, i.CanSync, i.DontSync)));

        var search = await ReadAsync<TrackerSelectableSeriesListDto>(await admin.GetAsync("/api/v1/trackers/mangaupdates/series/ids?q=series%20000"));
        Assert.Equal(6, search.TotalCount); // 0004-0009
        Assert.False(search.Items.Single(i => i.SeriesId == "sel-0005").CanSync); // no id
        var off = search.Items.Single(i => i.SeriesId == "sel-0006");
        Assert.Equal((false, true, TrackerActionBlock.DontSync), (off.CanSync, off.DontSync, off.ToTracker.Block));

        // All: the list's order (by title), capped at 2000 with the flag (the series SeedSeriesAsync made has no row: nothing was read).
        var all = await ReadAsync<TrackerSelectableSeriesListDto>(await admin.GetAsync("/api/v1/trackers/mangaupdates/series/ids"));
        Assert.Equal((total, TrackerFacts.MaxSelectableSeries, true), (all.TotalCount, all.Items.Count, all.Truncated));
        var page = await ReadAsync<TrackerSeriesPageDto>(await admin.GetAsync("/api/v1/trackers/mangaupdates/series"));
        Assert.Equal(page.Items.Select(r => r.SeriesId), all.Items.Take(page.Items.Count).Select(i => i.SeriesId));

        foreach (var bad in new[] { "?filter=Nope", "?q=" + new string('x', 201) })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/trackers/mangaupdates/series/ids" + bad)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/trackers/nope/series/ids")).StatusCode);

        // Another user: none of the admin's rows; a library the admin loses access to drops its series.
        await h.Factory.CreateUserAsync("other", "Other-Pass-12345", isAdmin: false);
        var other = await h.Factory.LoginAsync("other", "Other-Pass-12345");
        Assert.Equal(0, (await ReadAsync<TrackerSelectableSeriesListDto>(await other.GetAsync("/api/v1/trackers/mangaupdates/series/ids"))).TotalCount);
        await h.DbAsync(async db =>
        {
            var node = await db.CatalogNodes.Where(n => n.PublicId == "sel-node-0001").Select(n => n.Id).SingleAsync();
            return await db.NodeSeriesLinks.Where(l => l.NodeId == node).ExecuteDeleteAsync();
        });
        var after = await ReadAsync<TrackerSelectableSeriesListDto>(await admin.GetAsync("/api/v1/trackers/mangaupdates/series/ids?filter=Held"));
        Assert.Equal(new[] { "sel-0002", "sel-0003" }, after.Items.Select(i => i.SeriesId));
    }
}
