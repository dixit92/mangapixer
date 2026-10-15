namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The card ring (owner, 2026-10-15): "read on EVERY tracker this series syncs to" - an intersection. Synced = connected (account OK),
/// allowed by the admin, read at least once, on the user's list, not "Don't sync". A card carries the slugs of ALL synced trackers when every
/// one covers it, else nothing. Through the real browse (folder list and Volumes view) with FAKE tracker clients - no request to any tracker.
/// </summary>
public sealed class TrackerCardRingTests
{
    private static TrackerEntry Reading(int chapters) => new(TrackerEntryStatus.Reading, chapters, null, null, null, false, null, null);

    /// <summary>Both trackers allowed and connected; one series of <paramref name="chapters"/> chapter files.</summary>
    private static async Task<(TrackerTestHarness H, HttpClient Admin, TrackerSeriesSeed Series)> BothAsync(int chapters = 6)
    {
        var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        await h.AllowAsync(TrackerSlugs.MyAnimeList);
        await h.ConnectMangaUpdatesAsync(admin);
        await h.ConnectMalAsync(admin);
        return (h, admin, await h.SeedSeriesAsync(chapters));
    }

    /// <summary>A read mark on chapter 1 reads both trackers (each is ahead: nothing is written).</summary>
    private static async Task ReadBothAsync(TrackerTestHarness h, HttpClient admin, TrackerSeriesSeed series)
    {
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/v1/reading/{series.Archives[0].PublicId}/read", null)).StatusCode);
        await h.IdleAsync();
    }

    /// <summary>Each card's ring as "slug,slug", or null without one.</summary>
    private static async Task<Dictionary<string, string?>> RingsAsync(HttpClient client, TrackerSeriesSeed series, bool volumes = false)
    {
        var response = await client.GetAsync($"/api/v1/libraries/{series.LibraryPublicId}/browse?parentId={series.FolderPublicId}&pageSize=50"
            + (volumes ? "&group=volumes" : ""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("items").EnumerateArray().ToDictionary(
            e => e.GetProperty("displayName").GetString()!,
            e => e.TryGetProperty("trackerRead", out var r) ? string.Join(",", r.EnumerateArray().Select(x => x.GetString())) : null);
    }

    private static string Chapter(int n) => $"Chapter {n:000}";

    [Fact]
    [Trait("Category", "Unit")]
    public void TheRule_IsAnIntersection()
    {
        (string, TrackerNumbers)[] two = [("mal", new(4, null)), ("mangaupdates", new(2, null))];
        Assert.Equal(["mal", "mangaupdates"], TrackerCardMarks.ReadOnEvery(two, n => n.Chapters >= 2));
        Assert.Null(TrackerCardMarks.ReadOnEvery(two, n => n.Chapters >= 3));
        Assert.Equal(["mal"], TrackerCardMarks.ReadOnEvery([("mal", new(4, null))], n => n.Chapters >= 3));
        Assert.Null(TrackerCardMarks.ReadOnEvery([], _ => true));
    }

    [Fact]
    public async Task OneSyncedTracker_DecidesAlone()
    {
        var h = new TrackerTestHarness();
        await using var _ = h;
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        await h.ConnectMangaUpdatesAsync(admin);
        var series = await h.SeedSeriesAsync(5);
        h.MangaUpdates.Entries[series.MangaUpdatesId] = Reading(3);
        await ReadBothAsync(h, admin, series);

        var rings = await RingsAsync(admin, series);
        Assert.Equal("mangaupdates", rings[Chapter(3)]);
        Assert.Null(rings[Chapter(4)]);
    }

    [Fact]
    public async Task TwoTrackersThatBothCover_RingWithBoth_OneBehind_NoRing()
    {
        var (h, admin, series) = await BothAsync();
        await using var _ = h;
        h.MangaUpdates.Entries[series.MangaUpdatesId] = Reading(2);
        h.Mal.Entries[series.MalId] = Reading(4);
        await ReadBothAsync(h, admin, series);

        var rings = await RingsAsync(admin, series);
        Assert.Equal("mal,mangaupdates", rings[Chapter(1)]);
        Assert.Equal("mal,mangaupdates", rings[Chapter(2)]);
        Assert.Null(rings[Chapter(3)]); // read on MyAnimeList, not on MangaUpdates: no ring
        Assert.Null(rings[Chapter(5)]);

        // A tracker the admin no longer allows does not count: MyAnimeList decides alone.
        var off = await admin.PutAsJsonAsync("/api/v1/admin/trackers/mangaupdates", new UpdateTrackerSettingsRequest { Enabled = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal("mal", (await RingsAsync(admin, series))[Chapter(4)]);
    }

    [Fact]
    public async Task ADontSyncTracker_DoesNotBlock_TheOtherDecidesAlone()
    {
        var (h, admin, series) = await BothAsync();
        await using var _ = h;
        h.MangaUpdates.Entries[series.MangaUpdatesId] = Reading(2);
        h.Mal.Entries[series.MalId] = Reading(4);
        await ReadBothAsync(h, admin, series);
        Assert.Null((await RingsAsync(admin, series))[Chapter(4)]);

        await admin.PutAsJsonAsync($"/api/v1/trackers/mangaupdates/entries/{series.RecordPublicId}/dont-sync", new TrackerDontSyncRequest { DontSync = true });

        var rings = await RingsAsync(admin, series);
        Assert.Equal("mal", rings[Chapter(4)]);
        Assert.Null(rings[Chapter(5)]);
    }

    [Fact]
    public async Task ATrackerNotOnTheList_OrWithoutAnId_DoesNotBlock()
    {
        var (h, admin, series) = await BothAsync();
        await using var _ = h;
        // On none of the user's MangaUpdates lists (and "Add series I start" off, so nothing is created there).
        await admin.PutAsJsonAsync("/api/v1/trackers/mangaupdates/settings", new UpdateTrackerUserSettingsRequest { AddStartedSeries = false });
        h.Mal.Entries[series.MalId] = Reading(4);
        await ReadBothAsync(h, admin, series);
        Assert.Empty(h.MangaUpdates.Writes);

        var rings = await RingsAsync(admin, series);
        Assert.Equal("mal", rings[Chapter(4)]);
        Assert.Null(rings[Chapter(5)]);

        // A connected tracker that knows no id for the series (never read) does not block either.
        var other = await h.SeedSeriesAsync(4);
        await h.DbAsync(async db =>
        {
            var companion = await db.MetadataCompanions.SingleAsync(c => c.RecordId == other.RecordId);
            (await db.MetadataRecords.SingleAsync(r => r.Id == companion.CompanionRecordId)).CrossIdsJson = null;
            return await db.SaveChangesAsync();
        });
        h.MangaUpdates.Entries[other.MangaUpdatesId] = Reading(3);
        await ReadBothAsync(h, admin, other);
        Assert.Equal("mangaupdates", (await RingsAsync(admin, other))[Chapter(3)]);
    }

    [Fact]
    public async Task TheVolumesViewStackCard_FollowsTheSameRule()
    {
        var (h, admin, series) = await BothAsync(chapters: 8);
        await using var _ = h;
        h.MangaUpdates.Entries[series.MangaUpdatesId] = Reading(8);
        h.Mal.Entries[series.MalId] = Reading(4);
        using (var scope = h.Factory.Services.CreateScope())
        {
            var record = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().MetadataRecords.SingleAsync(r => r.Id == series.RecordId);
            await scope.ServiceProvider.GetRequiredService<VolumeMapService>().StoreAsync(record, VolumeMapSource.MangaDexAggregate, VolumeMapState.Ok,
                """[{"v":"1","c":["1","2","3","4"]},{"v":"2","c":["5","6","7","8"]}]""", null, 4.0, 2, CancellationToken.None);
        }
        await h.IdleAsync();
        await ReadBothAsync(h, admin, series);

        var rings = await RingsAsync(admin, series, volumes: true);
        Assert.Equal("mal,mangaupdates", rings["Volume 1"]);
        Assert.Null(rings["Volume 2"]); // MangaUpdates read it, MyAnimeList did not
    }
}
