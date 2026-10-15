namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// The one-shot fix (1.43.0, owner on the review instance): a single unnumbered archive that is the whole series - linked as its own
/// series, or the only archive of a linked folder - is chapter 1 / volume 1 for "Set progress from the tracker", for what MangaPixer
/// sends, and for the ring. FAKE tracker clients only.
/// </summary>
public sealed class TrackerOneShotHttpTests
{
    private static TrackerEntry OneOfOne(TrackerEntryStatus status = TrackerEntryStatus.Completed) => new(status, 1, 1, 1, 1, true, null, null);

    private static async Task<(TrackerTestHarness H, HttpClient Admin, long UserId)> ConnectedAsync()
    {
        var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        await h.ConnectMangaUpdatesAsync(admin);
        return (h, admin, await h.UserIdAsync("admin"));
    }

    private static async Task<TrackerFromTrackerResultDto> FromTrackerAsync(HttpClient client, TrackerSeriesSeed series)
    {
        var response = await client.PostAsync($"/api/v1/trackers/mangaupdates/entries/{series.RecordPublicId}/from-tracker", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TrackerFromTrackerResultDto>(TestJson.Web))!;
    }

    private static async Task<Dictionary<string, string?>> RingsAsync(HttpClient client, string libraryId, string? parentId)
    {
        var response = await client.GetAsync($"/api/v1/libraries/{libraryId}/browse?pageSize=50" + (parentId is null ? "" : $"&parentId={parentId}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("items").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!,
            e => e.TryGetProperty("trackerRead", out var r) ? string.Join(",", r.EnumerateArray().Select(x => x.GetString())) : null);
    }

    [Fact]
    public async Task AnArchiveAnchor_IsMarkedFromTheTracker_AndCountsOneOfOne()
    {
        var (h, admin, userId) = await ConnectedAsync();
        await using var _ = h;
        var oneShot = await h.SeedSeriesAsync(names: ["Lonely Story", "Other Story"], anchorFirstArchive: true);
        h.MangaUpdates.Entries[oneShot.MangaUpdatesId] = OneOfOne();

        var result = await FromTrackerAsync(admin, oneShot);

        Assert.Equal((1, 0), (result.Marked, result.AlreadyRead));
        Assert.Equal((1, 1, 1), (result.Entry.Chapters, result.Entry.Volumes, result.Entry.TotalChapters)); // "1 of 1 ch"
        var marked = await h.DbAsync(db => db.ReadMarks.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.ItemId).ToListAsync());
        Assert.Equal([oneShot.Archives[0].Id], marked); // only the anchor, never its unlinked neighbour
    }

    [Fact]
    public async Task ReadingAnArchiveAnchor_SendsOneOfOne_AndItsCardRings()
    {
        var (h, admin, _) = await ConnectedAsync();
        await using var _ = h;
        var oneShot = await h.SeedSeriesAsync(names: ["Lonely Story", "Other Story"], anchorFirstArchive: true);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/v1/reading/{oneShot.Archives[0].PublicId}/read", null)).StatusCode);
        await h.IdleAsync();

        var (_, write, _) = Assert.Single(h.MangaUpdates.Writes);
        Assert.Equal((1, 1), (write.ChaptersRead, write.VolumesRead));
        var rings = await RingsAsync(admin, oneShot.LibraryPublicId, oneShot.FolderPublicId);
        Assert.Equal("mangaupdates", rings[oneShot.Archives[0].PublicId]);
        Assert.Null(rings[oneShot.Archives[1].PublicId]);
    }

    [Fact]
    public async Task AFolderWithExactlyOneUnnumberedArchive_BehavesTheSame()
    {
        var (h, admin, _) = await ConnectedAsync();
        await using var _ = h;
        var single = await h.SeedSeriesAsync(names: ["Lonely Story"]);
        h.MangaUpdates.Entries[single.MangaUpdatesId] = OneOfOne(TrackerEntryStatus.Reading);

        Assert.Equal(1, (await FromTrackerAsync(admin, single)).Marked);
        Assert.Equal("mangaupdates", (await RingsAsync(admin, single.LibraryPublicId, null))[single.FolderPublicId]); // the folder ring
        Assert.Equal("mangaupdates", (await RingsAsync(admin, single.LibraryPublicId, single.FolderPublicId))[single.Archives[0].PublicId]);

        // Read here first (another series): one write of 1 / 1.
        var other = await h.SeedSeriesAsync(names: ["Lonely Story"]);
        await admin.PutAsync($"/api/v1/reading/{other.Archives[0].PublicId}/read", null);
        await h.IdleAsync();
        var (mediaId, write, _) = Assert.Single(h.MangaUpdates.Writes);
        Assert.Equal((other.MangaUpdatesId, 1, 1), (mediaId, write.ChaptersRead, write.VolumesRead));
    }

    [Fact]
    public async Task AnUnnumberedArchiveNextToAnother_StaysUncounted()
    {
        var (h, admin, _) = await ConnectedAsync();
        await using var _ = h;
        var series = await h.SeedSeriesAsync(names: ["Chapter 001", "Extras"]);
        h.MangaUpdates.Entries[series.MangaUpdatesId] = new TrackerEntry(TrackerEntryStatus.Reading, 5, 1, null, null, false, null, null);

        var result = await FromTrackerAsync(admin, series);

        Assert.Equal(1, result.Marked); // Chapter 001 only
        Assert.Equal((1, (int?)null), (result.Entry.Chapters, result.Entry.Volumes));
        var rings = await RingsAsync(admin, series.LibraryPublicId, series.FolderPublicId);
        Assert.Equal("mangaupdates", rings[series.Archives[0].PublicId]);
        Assert.Null(rings[series.Archives[1].PublicId]);
        Assert.Null((await RingsAsync(admin, series.LibraryPublicId, null))[series.FolderPublicId]); // "Extras" is never covered
    }
}
