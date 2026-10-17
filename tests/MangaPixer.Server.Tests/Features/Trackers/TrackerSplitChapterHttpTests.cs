namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.44.0 (owner, 2026-10-17): a series whose newest chapters are split files (33.1 + 33.2 ... 37.1 + 37.2) - the sync counts them, the gold
/// ring covers their parts and "Set progress from the tracker to MangaPixer" marks them. Through the real relay and HTTP surface with FAKE
/// tracker clients (no network); names are synthetic, in the owner's shape.
/// </summary>
public sealed class TrackerSplitChapterHttpTests
{
    private static readonly IReadOnlyList<string> Names = Enumerable.Range(1, 32).Select(c => $"Ch. {c:0000}.00.cbz")
        .Concat(Enumerable.Range(33, 5).SelectMany(c => new[] { $"Ch. {c:0000}.10.cbz", $"Ch. {c:0000}.20.cbz" })).ToList();

    private static TrackerEntry Reading(int chapters) => new(TrackerEntryStatus.Reading, chapters, null, null, null, false, null, null);

    private static async Task<(TrackerTestHarness H, HttpClient Admin, long UserId, TrackerSeriesSeed Series)> SeriesAsync()
    {
        var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        await h.ConnectMangaUpdatesAsync(admin);
        return (h, admin, await h.UserIdAsync("admin"), await h.SeedSeriesAsync(names: Names));
    }

    [Fact]
    public async Task EverySplitPartRead_TheSyncSendsTheLastSplitChapter()
    {
        var (h, _, userId, series) = await SeriesAsync();
        await using var _ = h;
        h.MangaUpdates.Entries[series.MangaUpdatesId] = Reading(30);

        await h.DbAsync(async db =>
        {
            foreach (var (id, _) in series.Archives)
                db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = id, MarkedAt = DateTimeOffset.UtcNow, Source = "manual" });
            return await db.SaveChangesAsync();
        });
        await h.IdleAsync();

        Assert.Equal(37, h.MangaUpdates.Writes.Last().Write.ChaptersRead);
        var entry = await h.EntryAsync(userId, TrackerSlugs.MangaUpdates, series.RecordId);
        Assert.Equal(37, entry!.LocalChapters);
    }

    [Fact]
    public async Task TheTrackerAtTheLastSplitChapter_RingsItsParts_AndSetProgressFromItMarksThem()
    {
        var (h, admin, userId, series) = await SeriesAsync();
        await using var _ = h;
        h.MangaUpdates.Entries[series.MangaUpdatesId] = Reading(37);

        // "Set progress from MangaUpdates to MangaPixer": every archive, the parts of 37 included.
        var response = await admin.PostAsync($"/api/v1/trackers/mangaupdates/entries/{series.RecordPublicId}/from-tracker", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<TrackerFromTrackerResultDto>(TestJson.Web))!;
        await h.IdleAsync();
        Assert.Equal(Names.Count, result.Marked);
        Assert.Equal(Names.Count, await h.DbAsync(db => db.ReadMarks.CountAsync(m => m.UserId == userId)));

        // The cards of 37.1 and 37.2 ring like every other.
        var browse = await admin.GetAsync($"/api/v1/libraries/{series.LibraryPublicId}/browse?parentId={series.FolderPublicId}&pageSize=100&group=flat");
        using var json = JsonDocument.Parse(await browse.Content.ReadAsStringAsync());
        var rings = json.RootElement.GetProperty("items").EnumerateArray().ToDictionary(
            e => e.GetProperty("displayName").GetString()!,
            e => e.TryGetProperty("trackerRead", out var r) && r.ValueKind == JsonValueKind.Array);
        Assert.Equal(Names.Count, rings.Count);
        Assert.True(rings["Ch. 0037.10.cbz"]);
        Assert.True(rings["Ch. 0037.20.cbz"]);
        Assert.All(rings.Values, Assert.True);
    }
}
