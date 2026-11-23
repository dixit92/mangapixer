namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;
using com.lifepixer.mangapixer.Server.Features.Trackers.Adapters;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// HTTP tests (the running host, 1.54.0 community scores): the MyAnimeList score pass is wired (DI, the tick's services), reads only the
/// MyAnimeList ids the linked series' MangaDex records name, only behind BOTH gates (the metadata fetcher's automatic consent and
/// MyAnimeList allowed for this server with an app), with the app's client id and nothing else; and "Delete stored MyAnimeList scores"
/// (admin only, audited) clears them. The MyAnimeList client is a fake (no network).
/// </summary>
public sealed class MalCommunityScoreHttpTests
{
    private sealed class FakeMalScores : IMalCommunityScores
    {
        public List<(string? Client, string Id)> Calls { get; } = [];

        public Task<MalCommunityScore?> CommunityScoreAsync(TrackerAppSettings app, string malId, CancellationToken ct)
        {
            Calls.Add((app.ClientId, malId));
            return Task.FromResult<MalCommunityScore?>(new MalCommunityScore(8.62, 51000));
        }
    }

    private static async Task<MalScorePassResult?> RunAsync(TrackerTestHarness h)
    {
        using var scope = h.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MalScorePass>().RunAsync();
    }

    [Fact]
    public async Task TheScorePass_ReadsLinkedMalIdsOnlyBehindBothGates_AndAnAdminCanDeleteTheScores()
    {
        var fake = new FakeMalScores();
        await using var h = new TrackerTestHarness(new Dictionary<string, string?> { ["Metadata:NetworkDisabled"] = "false" }, services =>
        {
            services.RemoveAll<IMalCommunityScores>();
            services.AddSingleton<IMalCommunityScores>(fake);
        });
        var seed = await h.SeedSeriesAsync();
        await h.DbAsync(async db =>
        {
            (await db.Libraries.SingleAsync(l => l.Id == seed.LibraryId)).MetadataEnabled = true;
            return await db.SaveChangesAsync();
        });
        var admin = await h.AdminAsync();

        // MyAnimeList allowed, but the metadata fetcher's automatic consent not given: nothing is sent.
        await h.AllowAsync(TrackerSlugs.MyAnimeList);
        Assert.Null(await RunAsync(h));

        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest
        {
            FetchEnabled = true,
            AcceptedConsentVersion = MetadataConsent.CurrentVersion,
            AutoMatchEnabled = true,
            AcceptedAutoConsentVersion = MetadataAutoConsent.CurrentVersion,
        })).EnsureSuccessStatusCode();
        Assert.Equal(new MalScorePassResult(1, 1, null), await RunAsync(h));
        Assert.Equal([("synthetic-client", seed.MalId)], fake.Calls); // the app's client id and the record's own MAL id - nothing else
        var stored = await h.DbAsync(db => db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Provider == "mangadex"));
        Assert.Equal((8.62, 51000), (stored.MalScore!.Value, stored.MalVotes!.Value));
        Assert.Null((await h.DbAsync(db => db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Provider == "mangaupdates"))).MalScore);

        // Read again only after 30 days.
        Assert.Equal(new MalScorePassResult(0, 0, null), await RunAsync(h));
        Assert.Single(fake.Calls);

        // An admin deletes every stored MyAnimeList score (audited, count only).
        var delete = await admin.DeleteAsync("/api/v1/admin/metadata/community-scores/myanimelist");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal(1, (await delete.Content.ReadFromJsonAsync<MalScoresDeletedDto>(TestJson.Web))!.Records);
        var cleared = await h.DbAsync(db => db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Provider == "mangadex"));
        Assert.Equal((null, null, null), (cleared.MalScore, cleared.MalVotes, cleared.MalScoreAt));
        Assert.True(await h.DbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == AuditActions.MetadataMalScoresDelete)));

        // Disallowing MyAnimeList stops the pass.
        (await admin.PutAsJsonAsync($"/api/v1/admin/trackers/{TrackerSlugs.MyAnimeList}", new UpdateTrackerSettingsRequest { Enabled = false }))
            .EnsureSuccessStatusCode();
        Assert.Null(await RunAsync(h));
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task DeletingTheScores_IsForAdminsOnly()
    {
        await using var h = new TrackerTestHarness();
        using var anonymous = h.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/api/v1/admin/metadata/community-scores/myanimelist")).StatusCode);
    }
}
