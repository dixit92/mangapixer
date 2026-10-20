namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.45.1 (B4): <c>GET /api/v1/trackers/card-marks</c> - the browse page's re-read of the gold "read on &lt;tracker&gt;" rings after Back from
/// the reader, instead of replaying every page of the listing twice. HTTP through the app: the same ring as the listing for each card id (the
/// series' archives in its folder, the series folder at the root), the tracker's newer numbers without a listing, unknown ids left out, at most
/// 200 ids, and nothing for a viewer who cannot open the library. The tracker's numbers are seeded as stored entries (no tracker request).
/// </summary>
public sealed class TrackerCardMarksHttpTests
{
    private static async Task<(TrackerTestHarness H, HttpClient Admin, long UserId)> ConnectedAsync()
    {
        var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        await h.ConnectMangaUpdatesAsync(admin);
        return (h, admin, await h.UserIdAsync("admin"));
    }

    private static Task SeenAsync(TrackerTestHarness h, long userId, TrackerSeriesSeed series, int chapters) =>
        h.DbAsync(async db =>
        {
            var entry = await db.TrackerEntries.FirstOrDefaultAsync(e => e.UserId == userId && e.RecordId == series.RecordId);
            if (entry is null)
            {
                db.TrackerEntries.Add(entry = new TrackerEntryEntity
                {
                    UserId = userId,
                    Tracker = TrackerSlugs.MangaUpdates,
                    RecordId = series.RecordId,
                    State = (int)TrackerEntryState.Ok,
                    RemoteId = series.MangaUpdatesId,
                });
            }
            entry.RemoteChapters = chapters;
            entry.RemoteSeenAt = DateTimeOffset.UtcNow;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            return await db.SaveChangesAsync();
        });

    private static string? Ring(JsonElement card) =>
        card.TryGetProperty("trackerRead", out var r) && r.ValueKind == JsonValueKind.Array ? string.Join(",", r.EnumerateArray().Select(x => x.GetString())) : null;

    /// <summary>The listing's ring per card id.</summary>
    private static async Task<Dictionary<string, string?>> ListingAsync(HttpClient client, TrackerSeriesSeed series, string? parentId)
    {
        var url = $"/api/v1/libraries/{series.LibraryPublicId}/browse?pageSize=50" + (parentId is null ? string.Empty : "&parentId=" + parentId);
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("items").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, Ring, StringComparer.Ordinal);
    }

    private static async Task<(HttpStatusCode Status, Dictionary<string, string?> Rings)> CardMarksAsync(HttpClient client, string libraryId,
        string? parentId, IEnumerable<string> ids)
    {
        var url = $"/api/v1/trackers/card-marks?libraryId={libraryId}" + (parentId is null ? string.Empty : "&parentId=" + parentId)
            + string.Concat(ids.Select(id => "&ids=" + Uri.EscapeDataString(id)));
        var response = await client.GetAsync(url);
        if (response.StatusCode != HttpStatusCode.OK)
            return (response.StatusCode, []);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode,
            json.RootElement.GetProperty("items").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, Ring, StringComparer.Ordinal));
    }

    [Fact]
    public async Task CardMarks_AnswerTheListingsRing_PerCard_AndFollowTheTrackersNewNumbers_WithoutAListing()
    {
        var (h, admin, userId) = await ConnectedAsync();
        await using var _ = h;
        var series = await h.SeedSeriesAsync(3);
        await SeenAsync(h, userId, series, 2);
        var archiveIds = series.Archives.Select(a => a.PublicId).ToList();

        // In the series folder: each archive card's ring, exactly as the listing computes it (two of three are covered).
        var listing = await ListingAsync(admin, series, series.FolderPublicId);
        var (status, marks) = await CardMarksAsync(admin, series.LibraryPublicId, series.FolderPublicId, [.. archiveIds, "nope", "v:nope.1"]);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(archiveIds.Order(StringComparer.Ordinal), marks.Keys.Order(StringComparer.Ordinal)); // unknown ids left out
        foreach (var id in archiveIds)
            Assert.Equal(listing[id], marks[id]);
        Assert.Equal(2, marks.Values.Count(r => r == "mangaupdates"));

        // At the root: the series folder card's rollup - no ring until the tracker covers every archive, then the ring, with no listing.
        (status, marks) = await CardMarksAsync(admin, series.LibraryPublicId, null, [series.FolderPublicId]);
        Assert.Equal((HttpStatusCode.OK, (string?)null), (status, marks[series.FolderPublicId]));
        await SeenAsync(h, userId, series, 3);
        (status, marks) = await CardMarksAsync(admin, series.LibraryPublicId, null, [series.FolderPublicId]);
        Assert.Equal("mangaupdates", marks[series.FolderPublicId]);
        Assert.Equal((await ListingAsync(admin, series, null))[series.FolderPublicId], marks[series.FolderPublicId]);
        (_, marks) = await CardMarksAsync(admin, series.LibraryPublicId, series.FolderPublicId, archiveIds);
        Assert.All(archiveIds, id => Assert.Equal("mangaupdates", marks[id]));
    }

    [Fact]
    public async Task CardMarks_RefuseMoreThan200Ids_AnUnknownLibraryOrFolder_AndAViewerWhoCannotOpenTheLibrary()
    {
        var (h, admin, userId) = await ConnectedAsync();
        await using var _ = h;
        var series = await h.SeedSeriesAsync(2);
        await SeenAsync(h, userId, series, 2);
        var ids = series.Archives.Select(a => a.PublicId).ToList();

        Assert.Equal(HttpStatusCode.OK, (await CardMarksAsync(admin, series.LibraryPublicId, series.FolderPublicId,
            [.. ids, .. Enumerable.Range(0, 198).Select(i => "x" + i)])).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CardMarksAsync(admin, series.LibraryPublicId, series.FolderPublicId,
            [.. ids, .. Enumerable.Range(0, 199).Select(i => "x" + i)])).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/trackers/card-marks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CardMarksAsync(admin, "no-such-library", null, ids)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CardMarksAsync(admin, series.LibraryPublicId, ids[0], ids)).Status); // not a folder

        // A viewer without access to the library learns nothing about its nodes.
        await h.Factory.CreateUserAsync("plain", "Plain-Pass-12345", isAdmin: false);
        var plain = await h.Factory.LoginAsync("plain", "Plain-Pass-12345");
        Assert.Equal(HttpStatusCode.NotFound, (await CardMarksAsync(plain, series.LibraryPublicId, series.FolderPublicId, ids)).Status);
        // Signed out: the cookie is required (a token is refused - ApiTokenHttpTests.Sweep_TrackerRoutes_*).
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Factory.CreateClient().GetAsync(
            $"/api/v1/trackers/card-marks?libraryId={series.LibraryPublicId}&ids={ids[0]}")).StatusCode);
    }
}
