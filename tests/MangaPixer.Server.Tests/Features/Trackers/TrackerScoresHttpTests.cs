namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.53.0 (lane T, vault note "MangaPixer - Tracker Ratings"): the user's own tracker score through the real pipeline - the switch on the
/// account card (<c>PUT /trackers/{slug}/settings</c>), send on change (a rating route -> the rating-change sink -> the relay -> ONE read
/// and at most one write on a FAKE tracker client), its rules (only a linked folder, only an existing entry, Private / doujinshi / switch-off,
/// unchanged score, the daily cap, slow-down, a refused sign-in), "Take my scores" (fill / same / differ, never overwrite), "Use
/// &lt;tracker&gt;'s", consent 2, and two users with an Incognito session. No request to any real tracker.
/// </summary>
public sealed class TrackerScoresHttpTests
{
    private static async Task<(TrackerTestHarness H, HttpClient Admin, long UserId, TrackerSeriesSeed Series)> MalAsync(
        bool syncScores = true, MetadataFormat? format = null)
    {
        var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MyAnimeList);
        await h.ConnectMalAsync(admin);
        if (syncScores)
            await SwitchAsync(admin, TrackerSlugs.MyAnimeList, true);
        var series = await h.SeedSeriesAsync(3, format);
        return (h, admin, await h.UserIdAsync("admin"), series);
    }

    private static TrackerEntry OnList(int? score = null) =>
        new(TrackerEntryStatus.Reading, 1, null, null, null, false, null, null) { Score = score };

    private static async Task<TrackerAccountDto> SwitchAsync(HttpClient client, string tracker, bool on)
    {
        var response = await client.PutAsJsonAsync($"/api/v1/trackers/{tracker}/settings", new UpdateTrackerUserSettingsRequest { SyncScores = on });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TrackerAccountDto>(TestJson.Web))!;
    }

    private static Task<HttpResponseMessage> RateAsync(HttpClient client, string nodePublicId, int score) =>
        client.PutAsJsonAsync($"/api/v1/nodes/{nodePublicId}/rating", new { score });

    private static async Task<int?> RatingAsync(TrackerTestHarness h, long userId, long nodeId) =>
        await h.DbAsync(db => db.UserRatings.AsNoTracking().Where(r => r.UserId == userId && r.CatalogNodeId == nodeId)
            .Select(r => (int?)r.Score).FirstOrDefaultAsync());

    private static async Task SeedRatingAsync(TrackerTestHarness h, long userId, long nodeId, int score) =>
        await h.DbAsync(async db =>
        {
            db.UserRatings.Add(new UserRatingEntity { UserId = userId, CatalogNodeId = nodeId, Score = score, RatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });

    private static async Task<TrackerScoreImportResultDto> ImportAsync(HttpClient client, string tracker)
    {
        var response = await client.PostAsync($"/api/v1/trackers/{tracker}/scores/import", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TrackerScoreImportResultDto>(TestJson.Web))!;
    }

    // ---- the switch

    [Fact]
    public async Task TheSwitch_IsOffByDefault_PerTrackerAndPerUser()
    {
        var (h, admin, _, _) = await MalAsync(syncScores: false);
        await using var _h = h;
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        await h.ConnectMangaUpdatesAsync(admin);

        var overview = (await admin.GetFromJsonAsync<TrackersOverviewDto>("/api/v1/trackers", TestJson.Web))!;
        Assert.All(overview.Trackers, t => Assert.False(t.Account!.SyncScores));

        Assert.True((await SwitchAsync(admin, TrackerSlugs.MyAnimeList, true)).SyncScores);
        overview = (await admin.GetFromJsonAsync<TrackersOverviewDto>("/api/v1/trackers", TestJson.Web))!;
        Assert.True(overview.Trackers.Single(t => t.Slug == TrackerSlugs.MyAnimeList).Account!.SyncScores);
        Assert.False(overview.Trackers.Single(t => t.Slug == TrackerSlugs.MangaUpdates).Account!.SyncScores);
        // Another setting leaves it as it is.
        var other = await admin.PutAsJsonAsync("/api/v1/trackers/mal/settings", new UpdateTrackerUserSettingsRequest { AddStartedSeries = false });
        Assert.True((await other.Content.ReadFromJsonAsync<TrackerAccountDto>(TestJson.Web))!.SyncScores);
        Assert.False((await SwitchAsync(admin, TrackerSlugs.MyAnimeList, false)).SyncScores);
    }

    // ---- send on change

    [Fact]
    public async Task SendOnChange_ReadsFirst_WritesOnlyTheScore_AndIsIdempotent()
    {
        var (h, admin, _, series) = await MalAsync();
        await using var _h = h;
        h.Mal.Entries[series.MalId] = OnList();

        Assert.Equal(HttpStatusCode.NoContent, (await RateAsync(admin, series.FolderPublicId, 8)).StatusCode);
        await h.IdleAsync();
        Assert.Equal([series.MalId], h.Mal.Reads.ToArray());
        Assert.Equal([(series.MalId, (int?)8)], h.Mal.ScoreWrites.ToArray());
        Assert.Empty(h.Mal.Writes); // no progress write: the score goes alone

        // The tracker already holds 6 (changed there): rating 6 here reads and sends nothing.
        h.Mal.Entries[series.MalId] = OnList(6);
        await RateAsync(admin, series.FolderPublicId, 6);
        await h.IdleAsync();
        Assert.Equal(2, h.Mal.Reads.Count);
        Assert.Single(h.Mal.ScoreWrites);

        // A clear removes the score there (MyAnimeList: 0).
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/nodes/{series.FolderPublicId}/rating")).StatusCode);
        await h.IdleAsync();
        Assert.Equal((series.MalId, (int?)null), h.Mal.ScoreWrites.Last());
        Assert.Null(h.Mal.Entries[series.MalId].Score);
    }

    [Fact]
    public async Task SendOnChange_NeverCreatesAnEntry_NeverSendsAnArchive_AndNothingWithTheSwitchOff()
    {
        var (h, admin, _, series) = await MalAsync();
        await using var _h = h;

        // On none of the user's lists: one read, no write - a score never adds an entry.
        await RateAsync(admin, series.FolderPublicId, 7);
        await h.IdleAsync();
        Assert.Single(h.Mal.Reads);
        Assert.Empty(h.Mal.ScoreWrites);
        Assert.False(h.Mal.Entries.ContainsKey(series.MalId));

        // An archive's rating is never sent (not even read).
        h.Mal.Entries[series.MalId] = OnList();
        await RateAsync(admin, series.Archives[0].PublicId, 9);
        await h.IdleAsync();
        Assert.Single(h.Mal.Reads);

        // The switch off: nothing at all.
        await SwitchAsync(admin, TrackerSlugs.MyAnimeList, false);
        await RateAsync(admin, series.FolderPublicId, 3);
        await h.IdleAsync();
        Assert.Single(h.Mal.Reads);
        Assert.Empty(h.Mal.ScoreWrites);
    }

    [Fact]
    public async Task SendOnChange_FollowsTheProgressSyncRules_PrivateDoujinshiSwitchesAndDontSync()
    {
        var (h, admin, userId, series) = await MalAsync();
        await using var _h = h;
        h.Mal.Entries[series.MalId] = OnList();
        var dj = await h.SeedSeriesAsync(1, MetadataFormat.Doujinshi);
        h.Mal.Entries[dj.MalId] = OnList();

        // Doujinshi: not sent unless the user turned sync on for it.
        await RateAsync(admin, dj.FolderPublicId, 5);
        await h.IdleAsync();
        Assert.Empty(h.Mal.Reads);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/trackers/sync-overrides",
            new UpdateTrackerNodeOverridesRequest { NodeIds = [dj.FolderPublicId], Enabled = true })).StatusCode);
        await RateAsync(admin, dj.FolderPublicId, 6);
        await h.IdleAsync();
        Assert.Equal([(dj.MalId, (int?)6)], h.Mal.ScoreWrites.ToArray());

        // The user's Private library: not sent.
        await h.DbAsync(async db =>
        {
            db.PrivateLibraries.Add(new PrivateLibraryEntity { UserId = userId, LibraryId = series.LibraryId, MarkedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        await RateAsync(admin, series.FolderPublicId, 4);
        await h.IdleAsync();
        Assert.Single(h.Mal.ScoreWrites);

        // "Don't sync this series" on the tracker: not sent, even with sync turned on for the folder.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/trackers/sync-overrides",
            new UpdateTrackerNodeOverridesRequest { NodeIds = [series.FolderPublicId], Enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/trackers/mal/entries/{series.RecordPublicId}/dont-sync",
            new TrackerDontSyncRequest { DontSync = true })).StatusCode);
        await RateAsync(admin, series.FolderPublicId, 2);
        await h.IdleAsync();
        Assert.Single(h.Mal.ScoreWrites);
        Assert.DoesNotContain(series.MalId, h.Mal.Reads);
    }

    [Fact]
    public async Task SendOnChange_MangaUpdates_ReadsTheEntryThenTheRating_AndCountsEveryRequest()
    {
        var h = new TrackerTestHarness();
        await using var _h = h;
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        await h.ConnectMangaUpdatesAsync(admin);
        await SwitchAsync(admin, TrackerSlugs.MangaUpdates, true);
        var series = await h.SeedSeriesAsync(2);
        var userId = await h.UserIdAsync("admin");
        h.MangaUpdates.ScoreInEntry = false;
        h.MangaUpdates.Entries[series.MangaUpdatesId] = OnList();
        var before = await h.DbAsync(db => db.TrackerAccounts.Where(a => a.UserId == userId && a.Tracker == "mangaupdates").Select(a => a.RequestsUsed).SingleAsync());

        await RateAsync(admin, series.FolderPublicId, 9);
        await h.IdleAsync();
        Assert.Equal([series.MangaUpdatesId], h.MangaUpdates.Reads.ToArray());
        Assert.Equal([series.MangaUpdatesId], h.MangaUpdates.ScoreReads.ToArray());
        Assert.Equal([(series.MangaUpdatesId, (int?)9)], h.MangaUpdates.ScoreWrites.ToArray());
        var after = await h.DbAsync(db => db.TrackerAccounts.Where(a => a.UserId == userId && a.Tracker == "mangaupdates").Select(a => a.RequestsUsed).SingleAsync());
        Assert.Equal(before + 3, after);

        // The rating there already is 5 (changed on MangaUpdates): two reads, no write.
        h.MangaUpdates.SeparateScores[series.MangaUpdatesId] = 5;
        await RateAsync(admin, series.FolderPublicId, 5);
        await h.IdleAsync();
        Assert.Equal(2, h.MangaUpdates.ScoreReads.Count);
        Assert.Single(h.MangaUpdates.ScoreWrites);
    }

    [Fact]
    public async Task SendOnChange_TheDailyCapStopsIt_ARefusedSignInNeedsReconnect_AndASlowDownIsRetried()
    {
        var (h, admin, userId, series) = await MalAsync();
        await using var _h = h;
        h.Mal.Entries[series.MalId] = OnList();

        // Daily cap used up: no request.
        await h.DbAsync(async db =>
        {
            var account = await db.TrackerAccounts.SingleAsync(a => a.UserId == userId && a.Tracker == "mal");
            account.RequestsDay = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            account.RequestsUsed = TrackerFacts.DefaultDailyRequestCap;
            return await db.SaveChangesAsync();
        });
        await RateAsync(admin, series.FolderPublicId, 8);
        await h.IdleAsync();
        Assert.Empty(h.Mal.Reads);
        await h.DbAsync(async db =>
        {
            (await db.TrackerAccounts.SingleAsync(a => a.UserId == userId && a.Tracker == "mal")).RequestsUsed = 0;
            return await db.SaveChangesAsync();
        });

        // The tracker asks to slow down: the score is sent again after its delay (in-process).
        h.Mal.ScoreWriteFailures.Enqueue(new TrackerException(TrackerFailure.RateLimited, 429, TimeSpan.FromSeconds(1)));
        await RateAsync(admin, series.FolderPublicId, 7);
        await h.IdleAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (h.Mal.ScoreWrites.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        await h.IdleAsync();
        Assert.Equal([(series.MalId, (int?)7)], h.Mal.ScoreWrites.ToArray());

        // The sign-in is refused: the account needs a reconnect and nothing more is sent.
        h.Mal.ReadFailures.Enqueue(new TrackerException(TrackerFailure.NeedsReconnect, 401));
        await RateAsync(admin, series.FolderPublicId, 2);
        await h.IdleAsync();
        var state = await h.DbAsync(db => db.TrackerAccounts.Where(a => a.UserId == userId && a.Tracker == "mal").Select(a => a.State).SingleAsync());
        Assert.Equal((int)TrackerAccountState.NeedsReconnect, state);
        var reads = h.Mal.Reads.Count;
        await RateAsync(admin, series.FolderPublicId, 3);
        await h.IdleAsync();
        Assert.Equal(reads, h.Mal.Reads.Count);
        Assert.Single(h.Mal.ScoreWrites);
    }

    // ---- consent 2

    [Fact]
    public async Task Consent_AVersion1Account_NeedsRenewal_AndSendsNothingUntilItConnectsAgain()
    {
        var (h, admin, userId, series) = await MalAsync();
        await using var _h = h;
        h.Mal.Entries[series.MalId] = OnList();
        Assert.Equal(2, TrackerConsent.UserVersion(TrackerSlugs.MyAnimeList));
        Assert.Equal(2, TrackerConsent.UserVersion(TrackerSlugs.MangaUpdates));
        await h.DbAsync(async db =>
        {
            (await db.TrackerAccounts.SingleAsync(a => a.UserId == userId && a.Tracker == "mal")).ConsentVersion = 1;
            return await db.SaveChangesAsync();
        });

        var overview = (await admin.GetFromJsonAsync<TrackersOverviewDto>("/api/v1/trackers", TestJson.Web))!;
        Assert.True(overview.Trackers.Single(t => t.Slug == "mal").Account!.ConsentRenewalNeeded);
        await RateAsync(admin, series.FolderPublicId, 8);
        await h.IdleAsync();
        Assert.Empty(h.Mal.Reads);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/v1/trackers/mal/scores/import", null)).StatusCode);
        // Connecting with the version-1 text is refused; with the current one it works and sending resumes.
        var old = await admin.PostAsJsonAsync("/api/v1/trackers/mal/connect/start", new TrackerConnectStartRequest { AcceptedConsentVersion = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        Assert.Equal("consent_required", (await old.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        await h.ConnectMalAsync(admin);
        await RateAsync(admin, series.FolderPublicId, 6);
        await h.IdleAsync();
        Assert.Equal([(series.MalId, (int?)6)], h.Mal.ScoreWrites.ToArray());
    }

    [Fact]
    public async Task Consent_AServerOnAdminVersion1_StopsSyncing_UntilAnAdminAcceptsTheCurrentText()
    {
        var (h, admin, _, series) = await MalAsync();
        await using var _h = h;
        h.Mal.Entries[series.MalId] = OnList();
        Assert.Equal(2, TrackerConsent.AdminVersion(TrackerSlugs.MyAnimeList));
        Assert.Equal(2, TrackerConsent.AdminVersion(TrackerSlugs.MangaUpdates));
        await h.DbAsync(async db =>
        {
            (await db.TrackerSettings.SingleAsync(s => s.Tracker == "mal")).ConsentVersion = 1;
            return await db.SaveChangesAsync();
        });

        var server = (await admin.GetFromJsonAsync<AdminTrackerSettingsDto>("/api/v1/admin/trackers/mal", TestJson.Web))!;
        Assert.True(server.ConsentRenewalNeeded);
        Assert.Equal(2, server.CurrentConsentVersion);
        var overview = (await admin.GetFromJsonAsync<TrackersOverviewDto>("/api/v1/trackers", TestJson.Web))!;
        Assert.False(overview.Trackers.Single(t => t.Slug == "mal").Allowed);
        await RateAsync(admin, series.FolderPublicId, 8);
        await h.IdleAsync();
        Assert.Empty(h.Mal.Reads);

        // The admin text gate: version 1 is refused, the current version allows the tracker again.
        var old = await admin.PutAsJsonAsync("/api/v1/admin/trackers/mal", new UpdateTrackerSettingsRequest { Enabled = true, AcceptedConsentVersion = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        await h.AllowAsync(TrackerSlugs.MyAnimeList);
        await RateAsync(admin, series.FolderPublicId, 5);
        await h.IdleAsync();
        Assert.Equal([(series.MalId, (int?)5)], h.Mal.ScoreWrites.ToArray());
    }

    // ---- import

    [Fact]
    public async Task Import_FillsUnrated_KeepsSame_ListsDiffering_AndNeverSendsBack()
    {
        var (h, admin, userId, a) = await MalAsync();
        await using var _h = h;
        var b = await h.SeedSeriesAsync(1);
        var c = await h.SeedSeriesAsync(1);
        h.Mal.Entries[a.MalId] = OnList(8);      // unrated here -> filled
        h.Mal.Entries[b.MalId] = OnList(5);      // the same here -> nothing
        h.Mal.Entries[c.MalId] = OnList(9);      // 4 here -> kept and listed
        h.Mal.Entries["999001"] = OnList(10);    // no series here -> dropped
        h.Mal.Entries["999002"] = OnList();      // no score -> not read as one
        await SeedRatingAsync(h, userId, b.FolderId, 5);
        await SeedRatingAsync(h, userId, c.FolderId, 4);

        var result = await ImportAsync(admin, TrackerSlugs.MyAnimeList);
        await h.IdleAsync();

        Assert.Equal((4, 3, 1, 1), (result.Scored, result.Matched, result.Filled, result.Same));
        var differ = Assert.Single(result.Differ);
        Assert.Equal((c.RecordPublicId, c.FolderPublicId, 4, 9, "Synthetic Saga"), (differ.SeriesId, differ.NodeId, differ.MyScore, differ.TrackerScore, differ.Title));
        Assert.Equal(2, result.Requests); // pages of two scored entries in the fake
        Assert.False(result.CapReached);
        Assert.Equal(8, await RatingAsync(h, userId, a.FolderId));
        Assert.Equal(5, await RatingAsync(h, userId, b.FolderId));
        Assert.Equal(4, await RatingAsync(h, userId, c.FolderId)); // never overwritten silently
        // Taken FROM MyAnimeList: nothing is read or written back there.
        Assert.Empty(h.Mal.Reads);
        Assert.Empty(h.Mal.ScoreWrites);

        // "Use MyAnimeList's" for the listed row: the MangaPixer rating becomes 9, still nothing sent back.
        var use = await admin.PutAsJsonAsync($"/api/v1/trackers/mal/scores/{c.RecordPublicId}", new TrackerUseScoreRequest { Score = 9 });
        Assert.Equal(HttpStatusCode.NoContent, use.StatusCode);
        await h.IdleAsync();
        Assert.Equal(9, await RatingAsync(h, userId, c.FolderId));
        Assert.Empty(h.Mal.ScoreWrites);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync($"/api/v1/trackers/mal/scores/{c.RecordPublicId}", new TrackerUseScoreRequest { Score = 11 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PutAsJsonAsync("/api/v1/trackers/mal/scores/no-such-series", new TrackerUseScoreRequest { Score = 3 })).StatusCode);

        // A second import: everything is the same now.
        var again = await ImportAsync(admin, TrackerSlugs.MyAnimeList);
        Assert.Equal((0, 3, 0), (again.Filled, again.Same, again.Differ.Count));
    }

    [Fact]
    public async Task Import_FromOneTracker_IsStillSentToAnotherKeptInStep()
    {
        var (h, admin, userId, a) = await MalAsync();
        await using var _h = h;
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        await h.ConnectMangaUpdatesAsync(admin);
        await SwitchAsync(admin, TrackerSlugs.MangaUpdates, true);
        h.Mal.Entries[a.MalId] = OnList(7);
        h.MangaUpdates.Entries[a.MangaUpdatesId] = OnList();

        var result = await ImportAsync(admin, TrackerSlugs.MyAnimeList);
        await h.IdleAsync();

        Assert.Equal(1, result.Filled);
        Assert.Equal(7, await RatingAsync(h, userId, a.FolderId));
        Assert.Empty(h.Mal.ScoreWrites);
        Assert.Equal([(a.MangaUpdatesId, (int?)7)], h.MangaUpdates.ScoreWrites.ToArray());
    }

    [Fact]
    public async Task Import_StopsAtTheDailyCap_AndReportsIt()
    {
        var (h, admin, userId, a) = await MalAsync();
        await using var _h = h;
        h.Mal.Entries[a.MalId] = OnList(7);
        await h.DbAsync(async db =>
        {
            var account = await db.TrackerAccounts.SingleAsync(x => x.UserId == userId && x.Tracker == "mal");
            account.RequestsDay = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            account.RequestsUsed = TrackerFacts.DefaultDailyRequestCap;
            return await db.SaveChangesAsync();
        });

        var result = await ImportAsync(admin, TrackerSlugs.MyAnimeList);
        Assert.True(result.CapReached);
        Assert.Equal((0, 0), (result.Requests, result.Filled));
        Assert.Empty(h.Mal.ScorePages);
    }

    // ---- two users, Incognito

    [Fact]
    public async Task TwoUsers_EachOnlyTheirOwn_AndIncognitoLeavesThePrivateLibraryOutOfTheImport()
    {
        var (h, admin, adminId, shared) = await MalAsync();
        await using var _h = h;
        var hidden = await h.SeedSeriesAsync(1);
        var ungranted = await h.SeedSeriesAsync(1);
        var reader = await AccessTestUsers.ReaderAsync(h.Factory, admin, "score-reader", [shared.LibraryId, hidden.LibraryId], [hidden.LibraryId]);
        await h.ConnectMalAsync(reader.Client);

        // The admin's switch is not the reader's.
        var mine = (await reader.Client.GetFromJsonAsync<TrackersOverviewDto>("/api/v1/trackers", TestJson.Web))!;
        Assert.False(mine.Trackers.Single(t => t.Slug == "mal").Account!.SyncScores);

        // One fake list (the fake answers every user alike): the reader sees only their own libraries.
        h.Mal.Entries[shared.MalId] = OnList(6);
        h.Mal.Entries[hidden.MalId] = OnList(7);
        h.Mal.Entries[ungranted.MalId] = OnList(8);

        var underIncognito = await ImportAsync(reader.Incognito, TrackerSlugs.MyAnimeList);
        Assert.Equal((3, 1, 1), (underIncognito.Scored, underIncognito.Matched, underIncognito.Filled));
        Assert.Equal(6, await RatingAsync(h, reader.UserId, shared.FolderId));
        Assert.Null(await RatingAsync(h, reader.UserId, hidden.FolderId));
        var plain = await ImportAsync(reader.Client, TrackerSlugs.MyAnimeList);
        Assert.Equal((2, 1, 1), (plain.Matched, plain.Filled, plain.Same));
        Assert.Equal(7, await RatingAsync(h, reader.UserId, hidden.FolderId));
        Assert.Null(await RatingAsync(h, reader.UserId, ungranted.FolderId));
        // The admin's own ratings are untouched by the reader's import.
        Assert.Null(await RatingAsync(h, adminId, shared.FolderId));

        // "Use MyAnimeList's" on a series in a library the reader may not open: as unknown.
        Assert.Equal(HttpStatusCode.NotFound, (await reader.Client.PutAsJsonAsync($"/api/v1/trackers/mal/scores/{ungranted.RecordPublicId}",
            new TrackerUseScoreRequest { Score = 3 })).StatusCode);
        Assert.Null(await RatingAsync(h, reader.UserId, ungranted.FolderId));

        // The reader keeps their score in step; the admin's rating change sends nothing for the reader, and vice versa.
        await SwitchAsync(reader.Client, TrackerSlugs.MyAnimeList, true);
        await SwitchAsync(admin, TrackerSlugs.MyAnimeList, false);
        await RateAsync(admin, shared.FolderPublicId, 2);
        await h.IdleAsync();
        Assert.Empty(h.Mal.ScoreWrites);
        await RateAsync(reader.Client, shared.FolderPublicId, 3);
        await h.IdleAsync();
        var (_, score) = Assert.Single(h.Mal.ScoreWrites);
        Assert.Equal(3, score);
    }
}
