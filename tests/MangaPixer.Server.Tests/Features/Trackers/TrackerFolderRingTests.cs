namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Owner (2026-10-15, iPad review): a SERIES FOLDER card (its own Confirmed / Auto link) gets the ring when EVERY archive of the series the
/// viewer can open is covered by EVERY synced tracker - a rollup like the folder's Read badge, measured against the trackers. Through the real
/// browse at the library root; the tracker's last-seen numbers are seeded as stored entries (display only - no tracker request at all).
/// </summary>
public sealed class TrackerFolderRingTests
{
    private static async Task<(TrackerTestHarness H, HttpClient Admin, long UserId)> ConnectedAsync(bool mal = false)
    {
        var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        await h.ConnectMangaUpdatesAsync(admin);
        if (mal)
        {
            await h.AllowAsync(TrackerSlugs.MyAnimeList);
            await h.ConnectMalAsync(admin);
        }
        return (h, admin, await h.UserIdAsync("admin"));
    }

    /// <summary>The tracker's numbers as MangaPixer last read them (a synced entry).</summary>
    private static Task SeenAsync(TrackerTestHarness h, long userId, string tracker, TrackerSeriesSeed series, int? chapters, int? volumes = null) =>
        h.DbAsync(async db =>
        {
            db.TrackerEntries.Add(new TrackerEntryEntity
            {
                UserId = userId,
                Tracker = tracker,
                RecordId = series.RecordId,
                State = (int)TrackerEntryState.Ok,
                RemoteId = tracker == TrackerSlugs.MyAnimeList ? series.MalId : series.MangaUpdatesId,
                RemoteChapters = chapters,
                RemoteVolumes = volumes,
                RemoteSeenAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            return await db.SaveChangesAsync();
        });

    /// <summary>The ring of the series folder card at its library's root ("slug,slug"), or null.</summary>
    private static async Task<string?> FolderRingAsync(HttpClient client, TrackerSeriesSeed series)
    {
        var response = await client.GetAsync($"/api/v1/libraries/{series.LibraryPublicId}/browse?pageSize=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var card = json.RootElement.GetProperty("items").EnumerateArray().Single(e => e.GetProperty("id").GetString() == series.FolderPublicId);
        Assert.Equal("Folder", card.GetProperty("kind").GetString());
        return card.TryGetProperty("trackerRead", out var r) ? string.Join(",", r.EnumerateArray().Select(x => x.GetString())) : null;
    }

    [Fact]
    public async Task EveryArchiveCovered_OnOneTracker_Rings()
    {
        var (h, admin, userId) = await ConnectedAsync();
        await using var _ = h;
        var series = await h.SeedSeriesAsync(3);
        await SeenAsync(h, userId, TrackerSlugs.MangaUpdates, series, 3);

        Assert.Equal("mangaupdates", await FolderRingAsync(admin, series));
    }

    [Fact]
    public async Task OneArchiveBeyondTheTrackersNumber_NoRing()
    {
        var (h, admin, userId) = await ConnectedAsync();
        await using var _ = h;
        var series = await h.SeedSeriesAsync(4);
        await SeenAsync(h, userId, TrackerSlugs.MangaUpdates, series, 3);

        Assert.Null(await FolderRingAsync(admin, series));
    }

    [Fact]
    public async Task TwoTrackers_OneBehind_NoRing_BothCover_RingWithBoth()
    {
        var (h, admin, userId) = await ConnectedAsync(mal: true);
        await using var _ = h;
        var behind = await h.SeedSeriesAsync(3);
        await SeenAsync(h, userId, TrackerSlugs.MangaUpdates, behind, 3);
        await SeenAsync(h, userId, TrackerSlugs.MyAnimeList, behind, 2);
        var both = await h.SeedSeriesAsync(3);
        await SeenAsync(h, userId, TrackerSlugs.MangaUpdates, both, 3);
        await SeenAsync(h, userId, TrackerSlugs.MyAnimeList, both, 5);

        Assert.Null(await FolderRingAsync(admin, behind));
        Assert.Equal("mal,mangaupdates", await FolderRingAsync(admin, both));
    }

    [Fact]
    public async Task AnUntrackedSeries_HasNoRing_AndCostsNoArchiveWalk()
    {
        var (h, _, userId) = await ConnectedAsync();
        await using var __ = h;
        var tracked = await h.SeedSeriesAsync(2);
        var untracked = await h.SeedSeriesAsync(2);
        await SeenAsync(h, userId, TrackerSlugs.MangaUpdates, tracked, 9);

        static CatalogNodeDto Folder(TrackerSeriesSeed s) => new()
        {
            Id = s.FolderPublicId,
            ParentId = string.Empty,
            LibraryId = s.LibraryPublicId,
            Kind = CatalogNodeKind.Folder,
            DisplayName = "Synthetic Saga",
            Availability = CatalogNodeAvailability.Available,
        };

        using (var scope = h.Factory.Services.CreateScope())
        {
            var marks = scope.ServiceProvider.GetRequiredService<TrackerCardMarks>();
            var result = await marks.DecorateAsync([Folder(untracked)], userId, null, scope.ServiceProvider.GetRequiredService<VolumeEntryService>(),
                CancellationToken.None);
            Assert.Null(Assert.Single(result).TrackerRead);
            Assert.Equal(0, marks.SeriesWalked); // no archive walk for a series the viewer syncs nowhere

            result = await marks.DecorateAsync([Folder(untracked), Folder(tracked)], userId, null,
                scope.ServiceProvider.GetRequiredService<VolumeEntryService>(), CancellationToken.None);
            Assert.Equal(["mangaupdates"], result.Single(n => n.Id == tracked.FolderPublicId).TrackerRead!);
            Assert.Equal(1, marks.SeriesWalked);
        }

        // A viewer who tracks nothing: the one lookup, nothing else.
        await h.Factory.CreateUserAsync("plain", "Plain-Pass-12345", isAdmin: false);
        var plainId = await h.UserIdAsync("plain");
        using (var scope = h.Factory.Services.CreateScope())
        {
            var marks = scope.ServiceProvider.GetRequiredService<TrackerCardMarks>();
            await marks.DecorateAsync([Folder(tracked)], plainId, null, scope.ServiceProvider.GetRequiredService<VolumeEntryService>(),
                CancellationToken.None);
            Assert.Equal(0, marks.SeriesWalked);
        }
    }

    [Fact]
    public async Task AFolderWithNoArchives_HasNoRing()
    {
        var (h, admin, userId) = await ConnectedAsync();
        await using var _ = h;
        var empty = await h.SeedSeriesAsync(0);
        await SeenAsync(h, userId, TrackerSlugs.MangaUpdates, empty, 50);

        Assert.Null(await FolderRingAsync(admin, empty));
    }

    [Fact]
    public async Task VolumeFiles_RingByTheirVolumes_ButNotInADeclaredEdition()
    {
        var (h, admin, userId) = await ConnectedAsync();
        await using var _ = h;
        async Task<TrackerSeriesSeed> VolumesAsync()
        {
            var s = await h.SeedSeriesAsync(3);
            await h.DbAsync(async db =>
            {
                var ids = s.Archives.Select(a => a.Id).ToList();
                foreach (var node in await db.CatalogNodes.Where(n => ids.Contains(n.Id)).ToListAsync())
                    node.DisplayName = node.DisplayName.Replace("Chapter 00", "Volume 0", StringComparison.Ordinal);
                return await db.SaveChangesAsync();
            });
            await SeenAsync(h, userId, TrackerSlugs.MangaUpdates, s, null, volumes: 3);
            return s;
        }
        var plain = await VolumesAsync();
        var edition = await VolumesAsync();
        await h.DbAsync(async db =>
        {
            db.DeclaredFacts.Add(new DeclaredFactEntity
            {
                LibraryId = edition.LibraryId,
                NodeId = edition.FolderId,
                Key = DeclaredFactKeys.Volumes,
                Value = "3",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            return await db.SaveChangesAsync();
        });

        Assert.Equal("mangaupdates", await FolderRingAsync(admin, plain));
        // A declared edition numbers its own volumes: its volume files are never read by the tracker's volumes - no ring.
        Assert.Null(await FolderRingAsync(admin, edition));
    }
}
