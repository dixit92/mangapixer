namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The tracker sync engine through the real pipeline (1.43.0, lane E): the read-mark relay (a mark in the reader -> one read + at most one
/// write on a FAKE tracker client), the compare rules end to end, Private libraries and the user's sync switches, held bulk jumps,
/// upgrade marks, the OAuth state binding, secrets never leaving the server, user delete and a lost key ring. No request to any real
/// tracker: the clients are <see cref="FakeTrackerClient"/>s.
/// </summary>
public sealed class TrackerSyncHttpTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static async Task<(TrackerTestHarness H, HttpClient Admin, long UserId, TrackerSeriesSeed Series)> ConnectedAsync(
        int chapters = 10, MetadataFormat? format = null, MetadataOriginStatus status = MetadataOriginStatus.Ongoing)
    {
        var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        await h.ConnectMangaUpdatesAsync(admin);
        var series = await h.SeedSeriesAsync(chapters, format, status);
        return (h, admin, await h.UserIdAsync("admin"), series);
    }

    private static Task<HttpResponseMessage> MarkReadAsync(HttpClient client, string itemPublicId) =>
        client.PutAsync($"/api/v1/reading/{itemPublicId}/read", null);

    [Fact]
    public async Task AManualMark_SendsOneWrite_WithOnlyTheAllowedFields()
    {
        var (h, admin, userId, series) = await ConnectedAsync();
        await using var _ = h;

        Assert.Equal(HttpStatusCode.OK, (await MarkReadAsync(admin, series.Archives[4].PublicId)).StatusCode);
        await h.IdleAsync();

        Assert.Equal([series.MangaUpdatesId], h.MangaUpdates.Reads.ToArray());
        var (mediaId, write, token) = Assert.Single(h.MangaUpdates.Writes);
        Assert.Equal(series.MangaUpdatesId, mediaId);
        // A new entry: Reading, chapter 5 (a hole below does not hold it back), no volumes, the start day, no finish day, no private flag
        // (MangaUpdates has none) - and TrackerEntryWrite has no other field to send.
        Assert.Equal(new TrackerEntryWrite(TrackerEntryStatus.Reading, 5, null, Today, null, null), write);
        Assert.StartsWith(FakeTrackerClient.AccessPrefix, token, StringComparison.Ordinal);
        var entry = await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, series.RecordId);
        Assert.Equal((TrackerEntryState.Ok, 5, 5), ((TrackerEntryState)entry!.State, entry.LastSentChapters, entry.RemoteChapters));
    }

    [Fact]
    public async Task TrackerAhead_ZeroWrites_AndTheEntryShowsIt()
    {
        var (h, admin, userId, series) = await ConnectedAsync();
        await using var _ = h;
        h.MangaUpdates.Entries[series.MangaUpdatesId] = new TrackerEntry(TrackerEntryStatus.Reading, 40, null, null, null, false, null, null);

        await MarkReadAsync(admin, series.Archives[2].PublicId);
        await h.IdleAsync();

        Assert.Single(h.MangaUpdates.Reads);
        Assert.Empty(h.MangaUpdates.Writes);
        var entry = await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, series.RecordId);
        Assert.Equal((TrackerEntryState.TrackerAhead, 40, 3), ((TrackerEntryState)entry!.State, entry.RemoteChapters, entry.LocalChapters));
    }

    [Fact]
    public async Task Unread_ZeroWrites_ZeroRequests_AndShowsLowerHere()
    {
        var (h, admin, userId, series) = await ConnectedAsync();
        await using var _ = h;
        await MarkReadAsync(admin, series.Archives[5].PublicId);
        await h.IdleAsync();
        Assert.Single(h.MangaUpdates.Writes);

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/v1/reading/{series.Archives[5].PublicId}/read")).StatusCode);
        await MarkReadAsync(admin, series.Archives[1].PublicId);
        await h.IdleAsync();

        // The removal and the lower mark (2 < 6 sent) make no request at all; the tracker is never lowered automatically.
        Assert.Single(h.MangaUpdates.Reads);
        Assert.Single(h.MangaUpdates.Writes);
        var entry = await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, series.RecordId);
        Assert.Equal((TrackerEntryState.LowerHere, 2, 6), ((TrackerEntryState)entry!.State, entry.LocalChapters, entry.LastSentChapters));

        // "Set MangaUpdates to 2" is the user's click: it may lower.
        var set = await admin.PostAsJsonAsync($"/api/v1/trackers/mangaupdates/entries/{series.RecordPublicId}/set", new TrackerSetProgressRequest { Chapters = 2 });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(2, h.MangaUpdates.Writes.Last().Write.ChaptersRead);
    }

    [Fact]
    public async Task APrivateLibraryMark_QueuesNothing_AnOverriddenFolderInsideItDoes()
    {
        var (h, admin, userId, series) = await ConnectedAsync();
        await using var _ = h;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/v1/reading/private-libraries",
            new SetPrivateLibrariesRequest { LibraryIds = [series.LibraryPublicId] })).StatusCode);

        await MarkReadAsync(admin, series.Archives[0].PublicId);
        await h.IdleAsync();
        Assert.Equal(0, h.MangaUpdates.CallCount);
        Assert.Null(await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, series.RecordId));

        // The user turns sync on for the series folder inside the Private library (owner Q3): the next mark syncs.
        var on = await admin.PutAsJsonAsync("/api/v1/trackers/sync-overrides",
            new UpdateTrackerNodeOverridesRequest { NodeIds = [series.FolderPublicId], Enabled = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True(Assert.Single((await on.Content.ReadFromJsonAsync<List<TrackerNodeOverrideDto>>(TestJson.Web))!).Enabled);
        await MarkReadAsync(admin, series.Archives[1].PublicId);
        await h.IdleAsync();
        Assert.Equal(2, Assert.Single(h.MangaUpdates.Writes).Write.ChaptersRead);
    }

    [Fact]
    public async Task ASyncOffSwitch_AndDoujinshi_SendNothing_UntilTurnedOn()
    {
        var (h, admin, _, series) = await ConnectedAsync(format: MetadataFormat.Doujinshi);
        await using var __ = h;
        await MarkReadAsync(admin, series.Archives[0].PublicId);
        await h.IdleAsync();
        Assert.Equal(0, h.MangaUpdates.CallCount);

        var other = await h.SeedSeriesAsync();
        await admin.PutAsJsonAsync("/api/v1/trackers/sync-overrides", new UpdateTrackerNodeOverridesRequest { NodeIds = [other.FolderPublicId], Enabled = false });
        await MarkReadAsync(admin, other.Archives[0].PublicId);
        await h.IdleAsync();
        Assert.Equal(0, h.MangaUpdates.CallCount);

        await admin.PutAsJsonAsync("/api/v1/trackers/sync-overrides", new UpdateTrackerNodeOverridesRequest { NodeIds = [series.FolderPublicId], Enabled = true });
        await MarkReadAsync(admin, series.Archives[1].PublicId);
        await h.IdleAsync();
        Assert.Equal(series.MangaUpdatesId, Assert.Single(h.MangaUpdates.Writes).MediaId);
    }

    [Fact]
    public async Task ABulkMark_IsHeld_UntilSend()
    {
        var (h, admin, userId, series) = await ConnectedAsync();
        await using var _ = h;

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/v1/reading/folders/{series.FolderPublicId}/read", null)).StatusCode);
        await h.IdleAsync();

        Assert.Empty(h.MangaUpdates.Writes);
        var entry = await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, series.RecordId);
        Assert.Equal((TrackerEntryState.Held, 10), ((TrackerEntryState)entry!.State, entry.HeldChapters));

        var send = await admin.PostAsync($"/api/v1/trackers/mangaupdates/entries/{series.RecordPublicId}/send", null);
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        var dto = (await send.Content.ReadFromJsonAsync<TrackerEntryDto>(TestJson.Web))!;
        Assert.Equal((TrackerEntryState.Ok, 10, (int?)null), (dto.State, dto.LastSentChapters, dto.HeldChapters));
        Assert.Equal(10, Assert.Single(h.MangaUpdates.Writes).Write.ChaptersRead);
    }

    [Fact]
    public async Task AnUpgradeMark_SendsNothing()
    {
        var (h, _, userId, series) = await ConnectedAsync();
        await using var __ = h;

        await h.DbAsync(async db =>
        {
            db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = series.Archives[7].Id, MarkedAt = DateTimeOffset.UtcNow, Source = "upgrade" });
            return await db.SaveChangesAsync();
        });
        await h.IdleAsync();

        Assert.Equal(0, h.MangaUpdates.CallCount);
        Assert.Null(await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, series.RecordId));
    }

    [Fact]
    public async Task AnOngoingSeries_AtTheTrackersCount_IsNotSetCompleted()
    {
        var (h, admin, _, series) = await ConnectedAsync(chapters: 10, status: MetadataOriginStatus.Ongoing);
        await using var _ = h;
        // The tracker lists 10 chapters but the series is not finished there.
        h.MangaUpdates.Entries[series.MangaUpdatesId] = new TrackerEntry(TrackerEntryStatus.Reading, 9, null, 10, null, false, Today, null);

        await MarkReadAsync(admin, series.Archives[9].PublicId);
        await h.IdleAsync();

        var write = Assert.Single(h.MangaUpdates.Writes).Write;
        Assert.Equal((TrackerEntryStatus.Reading, 10, (DateOnly?)null), (write.Status, write.ChaptersRead, write.FinishedOn));
    }

    [Fact]
    public async Task AMalMark_UsesTheMangaDexRecordsMalId_AndRefreshesAnExpiringSignIn_StoringTheNewRefreshToken()
    {
        await using var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MyAnimeList);
        h.Mal.TokenLifetime = TimeSpan.FromMinutes(2);
        await h.ConnectMalAsync(admin);
        var series = await h.SeedSeriesAsync();
        var userId = await h.UserIdAsync("admin");

        await MarkReadAsync(admin, series.Archives[2].PublicId);
        await h.IdleAsync();

        var refreshed = Assert.Single(h.Mal.Refreshes);
        Assert.Equal(FakeTrackerClient.RefreshPrefix + "mal-1", refreshed.RefreshToken);
        var (mediaId, write, token) = Assert.Single(h.Mal.Writes);
        Assert.Equal((series.MalId, 3, FakeTrackerClient.AccessPrefix + "mal-2"), (mediaId, write.ChaptersRead, token));
        // The rotated refresh token was stored at once (encrypted).
        var stored = await h.DbAsync(db => db.TrackerAccounts.AsNoTracking().SingleAsync(a => a.UserId == userId && a.Tracker == TrackerSlugs.MyAnimeList));
        var protector = h.Factory.Services.GetRequiredService<TrackerTokenProtector>();
        Assert.Equal(FakeTrackerClient.RefreshPrefix + "mal-2", protector.TryUnprotect(stored.RefreshTokenProtected));
        Assert.DoesNotContain("SECRET", stored.RefreshTokenProtected!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStateFromAnotherSession_OrAnotherUser_IsRefused_AndAStateWorksOnce()
    {
        await using var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MyAnimeList);
        var otherSession = await h.Factory.LoginAsync("admin", ApiTokenTestFactory.AdminPassword);
        await h.Factory.CreateUserAsync("reader2", "Reader2-Password!", isAdmin: false);
        var otherUser = await h.Factory.LoginAsync("reader2", "Reader2-Password!");

        var (state, challenge) = await TrackerTestHarness.StartMalAsync(admin);

        Assert.Equal(HttpStatusCode.BadRequest, (await TrackerTestHarness.FinishMalAsync(otherSession, state)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TrackerTestHarness.FinishMalAsync(otherUser, state)).StatusCode);
        Assert.Empty(h.Mal.Connects);

        Assert.Equal(HttpStatusCode.OK, (await TrackerTestHarness.FinishMalAsync(admin, state)).StatusCode);
        // PKCE "plain": the verifier the tracker gets is the challenge the browser carried.
        Assert.Equal(challenge, Assert.Single(h.Mal.Connects).CodeVerifier);
        var replay = await TrackerTestHarness.FinishMalAsync(admin, state);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_state", (await replay.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        Assert.Single(h.Mal.Connects);
    }

    [Fact]
    public async Task TokensAndPasswords_NeverReachLogs_ResponsesTheExportOrTheDatabaseInClear()
    {
        var (h, admin, userId, series) = await ConnectedAsync();
        await using var _ = h;
        await h.AllowAsync(TrackerSlugs.MyAnimeList);
        await h.ConnectMalAsync(admin);
        await admin.PutAsJsonAsync("/api/v1/admin/trackers/mal", new UpdateTrackerSettingsRequest { ClientSecret = "CS-SECRET-synthetic" });
        await MarkReadAsync(admin, series.Archives[0].PublicId);
        await h.IdleAsync();
        Assert.NotEmpty(h.MangaUpdates.Writes);

        var bodies = new List<string>();
        foreach (var url in new[]
                 {
                     "/api/v1/trackers", "/api/v1/trackers/mal/entries", "/api/v1/trackers/mangaupdates/entries", "/api/v1/trackers/sync-overrides",
                     "/api/v1/admin/trackers", "/api/v1/admin/trackers/mal", "/api/v1/admin/trackers/accounts",
                     $"/api/v1/admin/trackers/nodes/{series.FolderPublicId}/ids", "/api/v1/admin/audit?page=1&pageSize=200",
                     $"/api/v1/export/metadata?library={series.LibraryPublicId}", "/api/v1/export/libraries", "/api/v1/operations/diagnostics", "/api/v1/operations/diagnostics/export",
                 })
        {
            var response = await admin.GetAsync(url);
            Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode}");
            bodies.Add(await response.Content.ReadAsStringAsync());
        }
        var everything = string.Join("\n", bodies) + "\n" + h.Factory.CapturedLogText();
        foreach (var secret in new[] { "PW-SECRET", "AT-SECRET", "RT-SECRET", "CS-SECRET", "synthetic-code" })
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);

        var stored = await h.DbAsync(async db => string.Join("\n",
            (await db.TrackerAccounts.AsNoTracking().ToListAsync()).SelectMany(a => new[] { a.AccessTokenProtected, a.RefreshTokenProtected, a.DisplayName })
            .Concat((await db.TrackerSettings.AsNoTracking().ToListAsync()).Select(s => s.ClientSecretProtected))
            .Concat((await db.TrackerOAuthStates.AsNoTracking().ToListAsync()).Select(s => s.VerifierProtected))));
        Assert.DoesNotContain("SECRET", stored, StringComparison.Ordinal);
        Assert.Equal(2, await h.DbAsync(db => db.TrackerAccounts.CountAsync(a => a.UserId == userId && a.AccessTokenProtected != null)));
        // The admin's list shows that the user connected - not the remote account.
        Assert.DoesNotContain("Synthetic Reader", bodies[6], StringComparison.Ordinal);
        Assert.Contains("Synthetic Saga", bodies[9], StringComparison.Ordinal); // the export answered with the series
    }

    [Fact]
    public async Task UserDelete_LeavesNoTrackerRows_AndRevokesTheSignIn()
    {
        var (h, admin, _, series) = await ConnectedAsync();
        await using var _ = h;
        await h.Factory.CreateUserAsync("reader3", "Reader3-Password!", isAdmin: true);
        var reader = await h.Factory.LoginAsync("reader3", "Reader3-Password!");
        var readerId = await h.UserIdAsync("reader3");
        await h.ConnectMangaUpdatesAsync(reader);
        await h.AllowAsync(TrackerSlugs.MyAnimeList);
        await TrackerTestHarness.StartMalAsync(reader);
        await reader.PutAsJsonAsync("/api/v1/trackers/sync-overrides", new UpdateTrackerNodeOverridesRequest { NodeIds = [series.FolderPublicId], Enabled = true });
        await MarkReadAsync(reader, series.Archives[0].PublicId);
        await h.IdleAsync();
        Assert.Equal(1, await h.DbAsync(db => db.TrackerEntries.CountAsync(e => e.UserId == readerId)));

        var users = await admin.GetFromJsonAsync<List<AdminUserDto>>("/api/v1/admin/users", TestJson.Web);
        var publicId = users!.Single(u => u.Username == "reader3").Id;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/admin/users/{publicId}")).StatusCode);

        var left = await h.DbAsync(async db =>
            await db.TrackerAccounts.CountAsync(a => a.UserId == readerId) + await db.TrackerEntries.CountAsync(e => e.UserId == readerId)
            + await db.TrackerRetries.CountAsync(r => r.UserId == readerId) + await db.TrackerNodeOverrides.CountAsync(o => o.UserId == readerId)
            + await db.TrackerOAuthStates.CountAsync(s => s.UserId == readerId));
        Assert.Equal(0, left);
        Assert.Single(h.MangaUpdates.Revokes);
    }

    [Fact]
    public async Task ADatabaseWithoutItsKeyRing_TurnsTheAccountNeedsReconnect_WithoutARequest()
    {
        var (h, admin, userId, series) = await ConnectedAsync();
        await using var _ = h;
        // A payload of ANOTHER key ring: what a database restored without its keys holds.
        var foreign = new EphemeralDataProtectionProvider().CreateProtector(TrackerTokenProtector.Purpose).Protect("AT-SECRET-foreign");
        await h.DbAsync(async db =>
        {
            var account = await db.TrackerAccounts.SingleAsync(a => a.UserId == userId);
            account.AccessTokenProtected = foreign;
            return await db.SaveChangesAsync();
        });

        await MarkReadAsync(admin, series.Archives[0].PublicId);
        await h.IdleAsync();
        await MarkReadAsync(admin, series.Archives[1].PublicId);
        await h.IdleAsync();

        Assert.Equal(0, h.MangaUpdates.CallCount);
        var account = await h.DbAsync(db => db.TrackerAccounts.AsNoTracking().SingleAsync(a => a.UserId == userId));
        Assert.Equal((TrackerAccountState.NeedsReconnect, "key_ring"), ((TrackerAccountState)account.State, account.LastFailureCode));
        var overview = await admin.GetFromJsonAsync<TrackersOverviewDto>("/api/v1/trackers", TestJson.Web);
        Assert.Equal(TrackerAccountState.NeedsReconnect, overview!.Trackers.Single(t => t.Slug == TrackerSlugs.MangaUpdates).Account!.State);
    }

    [Fact]
    public async Task ARateLimitedWrite_IsRetriedAfterTheTrackersDelay()
    {
        var (h, admin, userId, series) = await ConnectedAsync();
        await using var _ = h;
        h.MangaUpdates.WriteFailures.Enqueue(new TrackerException(TrackerFailure.RateLimited, 412, TimeSpan.FromSeconds(1)));

        await MarkReadAsync(admin, series.Archives[3].PublicId);
        await h.IdleAsync();
        Assert.Empty(h.MangaUpdates.Writes);
        Assert.Equal(TrackerEntryState.Waiting, (TrackerEntryState)(await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, series.RecordId))!.State);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (h.MangaUpdates.Writes.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        await h.IdleAsync();
        Assert.Equal(4, Assert.Single(h.MangaUpdates.Writes).Write.ChaptersRead);
        Assert.Equal(0, await h.DbAsync(db => db.TrackerRetries.CountAsync()));
        Assert.Equal(TrackerEntryState.Ok, (TrackerEntryState)(await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, series.RecordId))!.State);
    }

    [Fact]
    public async Task TheAdminGate_NeedsTheConsent_KeepsTheSecretWriteOnly_AndStopsSync()
    {
        await using var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();

        var refused = await admin.PutAsJsonAsync("/api/v1/admin/trackers/mal", new UpdateTrackerSettingsRequest { Enabled = true });
        Assert.Equal("consent_required", (await refused.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        var badRedirect = await admin.PutAsJsonAsync("/api/v1/admin/trackers/mal", new UpdateTrackerSettingsRequest { RedirectUri = "javascript:alert(1)" });
        Assert.Equal(HttpStatusCode.BadRequest, badRedirect.StatusCode);

        var userView = await admin.GetFromJsonAsync<TrackersOverviewDto>("/api/v1/trackers", TestJson.Web);
        Assert.All(userView!.Trackers, t => Assert.False(t.Allowed));
        var notAllowed = await admin.PostAsJsonAsync("/api/v1/trackers/mal/connect/start", new TrackerConnectStartRequest { AcceptedConsentVersion = 1 });
        Assert.Equal(HttpStatusCode.Conflict, notAllowed.StatusCode);

        await h.AllowAsync(TrackerSlugs.MyAnimeList);
        var saved = await admin.PutAsJsonAsync("/api/v1/admin/trackers/mal", new UpdateTrackerSettingsRequest { ClientSecret = "CS-SECRET-gate" });
        var dto = (await saved.Content.ReadFromJsonAsync<AdminTrackerSettingsDto>(TestJson.Web))!;
        Assert.True(dto is { Enabled: true, ClientSecretSet: true, ClientId: "synthetic-client", RedirectUri: TrackerTestHarness.RedirectUri });
        Assert.DoesNotContain("CS-SECRET", await (await admin.GetAsync("/api/v1/admin/trackers")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var consent = await admin.PostAsJsonAsync("/api/v1/trackers/mal/connect/start", new TrackerConnectStartRequest { AcceptedConsentVersion = 0 });
        Assert.Equal("consent_required", (await consent.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
    }

    [Fact]
    public async Task TheConfigSwitch_DisablesEveryTracker()
    {
        await using var h = new TrackerTestHarness(new Dictionary<string, string?> { [TrackerSettingsService.NetworkDisabledConfigKey] = "true" });
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);

        var overview = await admin.GetFromJsonAsync<TrackersOverviewDto>("/api/v1/trackers", TestJson.Web);
        Assert.All(overview!.Trackers, t => Assert.True(t is { Allowed: false, NetworkDisabledByConfig: true }));
        var connect = await admin.PostAsJsonAsync("/api/v1/trackers/mangaupdates/connect/password",
            new TrackerConnectPasswordRequest { Username = "u", Password = "p", AcceptedConsentVersion = 1 });
        Assert.Equal(HttpStatusCode.Conflict, connect.StatusCode);
        Assert.Empty(h.MangaUpdates.Connects);
    }

    [Fact]
    public async Task AnAdminDisconnect_DeletesTheTokens_AndTheUserSeesIt()
    {
        var (h, admin, userId, _) = await ConnectedAsync();
        await using var __ = h;
        var users = await admin.GetFromJsonAsync<List<AdminUserDto>>("/api/v1/admin/users", TestJson.Web);
        var publicId = users!.Single(u => u.Username == "admin").Id;

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/admin/trackers/mangaupdates/accounts/{publicId}/disconnect", null)).StatusCode);

        var account = await h.DbAsync(db => db.TrackerAccounts.AsNoTracking().SingleAsync(a => a.UserId == userId));
        Assert.Equal((TrackerAccountState.DisconnectedByAdmin, (string?)null, (string?)null),
            ((TrackerAccountState)account.State, account.AccessTokenProtected, account.DisplayName));
        Assert.Single(h.MangaUpdates.Revokes);
        var audit = await admin.GetStringAsync("/api/v1/admin/audit?page=1&pageSize=50");
        Assert.Contains(TrackerAuditActions.Disconnect, audit, StringComparison.Ordinal);
        Assert.Contains(TrackerAuditActions.Connect, audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePreview_ReadsWithoutWriting_AndSendAllSends()
    {
        var (h, admin, userId, series) = await ConnectedAsync();
        await using var _ = h;
        // Imported marks count but wait for "Send" (owner Q4): the relay holds them without a request; the preview then reads the tracker.
        await h.DbAsync(async db =>
        {
            foreach (var (id, _) in series.Archives.Take(6))
                db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = id, MarkedAt = DateTimeOffset.UtcNow, Source = "import" });
            return await db.SaveChangesAsync();
        });
        await h.IdleAsync();
        Assert.Equal(0, h.MangaUpdates.CallCount);
        Assert.Equal(TrackerEntryState.Held, (TrackerEntryState)(await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, series.RecordId))!.State);
        var readsBefore = h.MangaUpdates.Reads.Count;

        var preview = await admin.PostAsync("/api/v1/trackers/mangaupdates/preview", null);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var dto = (await preview.Content.ReadFromJsonAsync<TrackerPreviewDto>(TestJson.Web))!;
        var row = Assert.Single(dto.Entries);
        Assert.Equal((TrackerEntryState.Held, 6, true, 0), (row.State, row.HeldChapters, row.RemoteMissing, dto.Remaining));
        Assert.Equal(readsBefore + 1, h.MangaUpdates.Reads.Count);
        Assert.Empty(h.MangaUpdates.Writes);

        var sendAll = await admin.PostAsync("/api/v1/trackers/mangaupdates/entries/send-all", null);
        Assert.Equal(1, (await sendAll.Content.ReadFromJsonAsync<TrackerSendAllResultDto>(TestJson.Web))!.Sent);
        Assert.Equal(6, Assert.Single(h.MangaUpdates.Writes).Write.ChaptersRead);
    }

    [Fact]
    public async Task TheDailyCap_StopsRequests_AndADisabledUsersSyncPauses()
    {
        var (h, admin, userId, series) = await ConnectedAsync();
        await using var _ = h;
        await h.DbAsync(async db =>
        {
            var account = await db.TrackerAccounts.SingleAsync(a => a.UserId == userId);
            account.RequestsDay = TrackerAccountService.DayOf(DateTimeOffset.UtcNow);
            account.RequestsUsed = TrackerFacts.DailyRequestCap;
            return await db.SaveChangesAsync();
        });
        await MarkReadAsync(admin, series.Archives[0].PublicId);
        await h.IdleAsync();
        Assert.Equal(0, h.MangaUpdates.CallCount);
        var entry = await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, series.RecordId);
        Assert.Equal((TrackerEntryState.Waiting, "daily_cap"), ((TrackerEntryState)entry!.State, entry.ErrorCode));

        // A disabled user: the account and the mark stay, nothing is sent (an import can still add marks for them).
        await h.Factory.CreateUserAsync("reader4", "Reader4-Password!", isAdmin: true);
        var reader = await h.Factory.LoginAsync("reader4", "Reader4-Password!");
        await h.ConnectMangaUpdatesAsync(reader);
        var readerId = await h.UserIdAsync("reader4");
        await h.DbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == readerId)).IsActive = false;
            db.ReadMarks.Add(new ReadMarkEntity { UserId = readerId, ItemId = series.Archives[1].Id, MarkedAt = DateTimeOffset.UtcNow, Source = "manual" });
            return await db.SaveChangesAsync();
        });
        await h.IdleAsync();
        Assert.Equal(0, h.MangaUpdates.CallCount);
        Assert.Null(await h.EntryAsync(readerId, TrackerSlugs.MangaUpdates, series.RecordId));
    }

    [Fact]
    public async Task TheAdminsSeriesId_WinsOverTheStoredOne()
    {
        var (h, admin, _, series) = await ConnectedAsync();
        await using var _ = h;

        var set = await admin.PutAsJsonAsync($"/api/v1/admin/trackers/nodes/{series.FolderPublicId}/ids/mangaupdates",
            new UpdateTrackerSeriesIdRequest { RemoteId = "123456" });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var ids = (await set.Content.ReadFromJsonAsync<TrackerSeriesIdsDto>(TestJson.Web))!;
        Assert.Equal(("123456", TrackerIdSource.Admin), (ids.Ids.Single(i => i.Tracker == "mangaupdates").RemoteId, ids.Ids.Single(i => i.Tracker == "mangaupdates").Source));
        Assert.Equal((series.MalId, TrackerIdSource.MangaDex), (ids.Ids.Single(i => i.Tracker == "mal").RemoteId, ids.Ids.Single(i => i.Tracker == "mal").Source));
        var bad = await admin.PutAsJsonAsync($"/api/v1/admin/trackers/nodes/{series.FolderPublicId}/ids/mangaupdates",
            new UpdateTrackerSeriesIdRequest { RemoteId = "12a" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        await MarkReadAsync(admin, series.Archives[0].PublicId);
        await h.IdleAsync();
        Assert.Equal("123456", Assert.Single(h.MangaUpdates.Writes).MediaId);
    }
}
