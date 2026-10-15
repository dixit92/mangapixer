namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// "Check my list on MyAnimeList" reads the WHOLE list (1.43.0, owner option C): every page (one request each against the user's daily
/// cap), every entry matched to a series here BY TRACKER ID (only series the user can open), the tracker's numbers stored on the user's
/// rows as a single read would - nothing stored for an entry with no series here, nothing ever written to the tracker. FAKE clients only:
/// no request to any tracker.
/// </summary>
public sealed class TrackerListCheckTests
{
    private const string NotHere = "999999";

    private static TrackerEntry Reading(int chapters) => new(TrackerEntryStatus.Reading, chapters, null, 40, null, false, null, null);

    private static async Task<(TrackerTestHarness H, HttpClient Admin, long UserId)> MalAsync()
    {
        var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MyAnimeList);
        await h.ConnectMalAsync(admin);
        h.Mal.ListPageSize = 2;
        return (h, admin, await h.UserIdAsync("admin"));
    }

    private static async Task<TrackerPreviewDto> CheckAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/v1/trackers/mal/preview", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TrackerPreviewDto>(TestJson.Web))!;
    }

    private static async Task<TrackerSeriesPageDto> PageAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/trackers/mal/series")).Content.ReadFromJsonAsync<TrackerSeriesPageDto>(TestJson.Web))!;

    [Fact]
    public async Task TheWholeList_IsReadPageByPage_MatchedByTrackerId_WithoutPerSeriesReadsOrWrites()
    {
        var (h, admin, userId) = await MalAsync();
        await using var _ = h;
        var a = await h.SeedSeriesAsync(3);
        var b = await h.SeedSeriesAsync(3);
        var readHereOnly = await h.SeedSeriesAsync(3);
        h.Mal.Entries[a.MalId] = Reading(2);
        h.Mal.Entries[b.MalId] = Reading(5);
        h.Mal.Entries[NotHere] = Reading(9);
        // A series read here (an import waits for Send - no outbound read) that is on none of the user's lists.
        await h.DbAsync(async db =>
        {
            db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = readHereOnly.Archives[0].Id, MarkedAt = DateTimeOffset.UtcNow, Source = "import" });
            return await db.SaveChangesAsync();
        });
        await h.IdleAsync();
        var reads = h.Mal.Reads.Count;

        var check = await CheckAsync(admin);

        Assert.Equal((true, 3, 2, 2, 0, false), (check.ListRead, check.ListEntries, check.Matched, check.Created, check.Remaining, check.CapReached));
        Assert.Equal([0, 2], h.Mal.ListReads.ToArray()); // two pages
        Assert.Equal(reads, h.Mal.Reads.Count); // no per-series read
        Assert.Empty(h.Mal.Writes); // never a write
        var rows = (await PageAsync(admin)).Items.ToDictionary(r => r.SeriesId);
        Assert.Equal((TrackerListBucket.TrackerAhead, 2, 40), (rows[a.RecordPublicId].Bucket, rows[a.RecordPublicId].RemoteChapters, rows[a.RecordPublicId].TotalChapters));
        Assert.Equal(5, rows[b.RecordPublicId].RemoteChapters);
        Assert.True(rows[readHereOnly.RecordPublicId].RemoteMissing);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task AnEntryWithNoSeriesHere_LeavesNothingStored()
    {
        var (h, admin, userId) = await MalAsync();
        await using var _ = h;
        h.Mal.Entries[NotHere] = Reading(9);
        h.Mal.Entries["888888"] = Reading(1);

        var check = await CheckAsync(admin);

        Assert.Equal((2, 0, 0), (check.ListEntries, check.Matched, check.Created));
        Assert.Empty(await h.DbAsync(db => db.TrackerEntries.AsNoTracking().Where(e => e.UserId == userId).ToListAsync()));
    }

    [Fact]
    public async Task OnlySeriesTheUserCanOpen_AreMatched()
    {
        var (h, _, _) = await MalAsync();
        await using var _ = h;
        var open = await h.SeedSeriesAsync(2);
        var closed = await h.SeedSeriesAsync(2);
        await h.Factory.CreateUserAsync("reader", "Reader-Pass-12345", isAdmin: false);
        var readerId = await h.UserIdAsync("reader");
        await h.DbAsync(async db =>
        {
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = readerId, LibraryId = open.LibraryId, GrantedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        var reader = await h.Factory.LoginAsync("reader", "Reader-Pass-12345");
        await h.ConnectMalAsync(reader);
        h.Mal.Entries[open.MalId] = Reading(1);
        h.Mal.Entries[closed.MalId] = Reading(1);

        var check = await CheckAsync(reader);

        Assert.Equal((2, 1), (check.ListEntries, check.Matched));
        var stored = await h.DbAsync(db => db.TrackerEntries.AsNoTracking().Where(e => e.UserId == readerId).Select(e => e.RecordId).ToListAsync());
        Assert.Equal([open.RecordId], stored);
    }

    [Fact]
    public async Task ASeriesNeverReadHere_IsTrackerAhead_AndSetProgressFromTheTrackerMarksIt()
    {
        var (h, admin, userId) = await MalAsync();
        await using var _ = h;
        var series = await h.SeedSeriesAsync(4);
        h.Mal.Entries[series.MalId] = Reading(3);
        Assert.Empty((await PageAsync(admin)).Items);

        await CheckAsync(admin);

        var row = Assert.Single((await PageAsync(admin)).Items);
        Assert.Equal((TrackerListBucket.TrackerAhead, true), (row.Bucket, row.FromTracker.Possible));
        var pulled = await admin.PostAsync($"/api/v1/trackers/mal/entries/{series.RecordPublicId}/from-tracker", null);
        Assert.Equal(3, (await pulled.Content.ReadFromJsonAsync<TrackerFromTrackerResultDto>(TestJson.Web))!.Marked);
        Assert.Equal(3, await h.DbAsync(db => db.ReadMarks.CountAsync(m => m.UserId == userId)));
        Assert.Empty(h.Mal.Writes);
    }

    [Fact]
    public async Task TheDailyCap_StopsTheReadMidList()
    {
        var (h, admin, userId) = await MalAsync();
        await using var _ = h;
        h.Mal.ListPageSize = 1;
        var a = await h.SeedSeriesAsync(2);
        var b = await h.SeedSeriesAsync(2);
        h.Mal.Entries[a.MalId] = Reading(1);
        h.Mal.Entries[b.MalId] = Reading(1);
        await h.DbAsync(async db =>
        {
            var account = await db.TrackerAccounts.SingleAsync(x => x.UserId == userId && x.Tracker == TrackerSlugs.MyAnimeList);
            account.RequestsDay = TrackerAccountService.DayOf(DateTimeOffset.UtcNow);
            account.RequestsUsed = TrackerFacts.DefaultDailyRequestCap - 1;
            return await db.SaveChangesAsync();
        });

        var check = await CheckAsync(admin);

        Assert.Equal((true, false, 1, 1), (check.CapReached, check.ListRead, check.ListEntries, check.Matched));
        Assert.Single(h.Mal.ListReads);
    }

    [Fact]
    public async Task A401_TurnsTheAccountNeedsReconnect()
    {
        var (h, admin, userId) = await MalAsync();
        await using var _ = h;
        h.Mal.ListFailures.Enqueue(new TrackerException(TrackerFailure.NeedsReconnect, 401));

        var response = await admin.PostAsync("/api/v1/trackers/mal/preview", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("needs_reconnect", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var state = await h.DbAsync(db => db.TrackerAccounts.Where(x => x.UserId == userId && x.Tracker == TrackerSlugs.MyAnimeList).Select(x => x.State).SingleAsync());
        Assert.Equal((int)TrackerAccountState.NeedsReconnect, state);
    }

    [Fact]
    public async Task ATrackerWithoutAListRead_KeepsThePerSeriesCheck()
    {
        var (h, admin, userId) = await MalAsync();
        await using var _ = h;
        h.Mal.ListPageSize = null;
        var series = await h.SeedSeriesAsync(2);
        h.Mal.Entries[series.MalId] = Reading(1);
        await h.DbAsync(async db =>
        {
            db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = series.Archives[0].Id, MarkedAt = DateTimeOffset.UtcNow, Source = "import" });
            return await db.SaveChangesAsync();
        });
        await h.IdleAsync();

        var check = await CheckAsync(admin);

        Assert.Null(check.ListRead);
        Assert.Empty(h.Mal.ListReads);
        Assert.Contains(series.MalId, h.Mal.Reads);
    }
}
