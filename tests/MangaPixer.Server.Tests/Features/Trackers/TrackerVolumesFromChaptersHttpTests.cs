namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.44.0 (owner, 2026-10-17): MyAnimeList at 271 chapters / 25 volumes while 271 chapters finish volume 27 on the series' exact volume list.
/// The Tracking row offers "Set MyAnimeList's volumes to 27"; the user's click (one series, or a batch) reads the entry and writes ONLY the
/// volumes, with MyAnimeList's own status. Never for an entry left as the user put it. FAKE tracker clients (no network).
/// </summary>
public sealed class TrackerVolumesFromChaptersHttpTests
{
    private static async Task<(TrackerTestHarness H, HttpClient Admin, TrackerSeriesSeed Series)> OwnerShapeAsync(TrackerEntryStatus status)
    {
        var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MyAnimeList);
        await h.ConnectMalAsync(admin);
        var series = await h.SeedSeriesAsync(3);
        using (var scope = h.Factory.Services.CreateScope())
        {
            // 28 volumes of 10 chapters (volume 27 = 261-270).
            var volumes = string.Join(",", Enumerable.Range(1, 28).Select(v =>
                $$"""{"v":"{{v}}","c":[{{string.Join(",", Enumerable.Range((v - 1) * 10 + 1, 10).Select(c => $"\"{c}\""))}}]}"""));
            var record = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().MetadataRecords.SingleAsync(r => r.Id == series.RecordId);
            await scope.ServiceProvider.GetRequiredService<VolumeMapService>().StoreAsync(record, VolumeMapSource.MangaDexAggregate, VolumeMapState.Ok,
                $"[{volumes}]", null, 10.0, 28, CancellationToken.None);
        }
        await h.IdleAsync();
        h.Mal.Entries[series.MalId] = new TrackerEntry(status, 271, 25, null, null, false, new DateOnly(2024, 8, 20), null);
        // Sync now: the entry as MyAnimeList has it (nothing read here - nothing is written).
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/trackers/mal/entries/{series.RecordPublicId}/sync", null)).StatusCode);
        Assert.Empty(h.Mal.Writes);
        return (h, admin, series);
    }

    private static async Task<int?> OfferAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/trackers/mal/series?filter=All");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.True(row.TryGetProperty("volumesFromChapters", out var offer), "the row has no volumesFromChapters");
        return offer.ValueKind == JsonValueKind.Null ? null : offer.GetInt32();
    }

    [Fact]
    public async Task TheRowOffersTheVolume_AndTheClickWritesOnlyTheVolumes()
    {
        var (h, admin, series) = await OwnerShapeAsync(TrackerEntryStatus.Reading);
        await using var _ = h;
        Assert.Equal(27, await OfferAsync(admin));

        var response = await admin.PostAsync($"/api/v1/trackers/mal/entries/{series.RecordPublicId}/volumes-from-chapters", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var write = Assert.Single(h.Mal.Writes).Write;
        Assert.Equal(new TrackerEntryWrite(TrackerEntryStatus.Reading, null, 27, null, null, null), write);
        Assert.Null(await OfferAsync(admin)); // done: nothing left to offer
    }

    [Fact]
    public async Task AnEntryOnHold_IsNeverOffered_NorWritten()
    {
        var (h, admin, series) = await OwnerShapeAsync(TrackerEntryStatus.OnHold);
        await using var _ = h;
        Assert.Null(await OfferAsync(admin));

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/trackers/mal/entries/{series.RecordPublicId}/volumes-from-chapters", null)).StatusCode);
        Assert.Empty(h.Mal.Writes);
    }

    [Fact]
    public async Task TheBatch_RaisesTheVolumes_AndSkipsWhatIsUpToDate()
    {
        var (h, admin, series) = await OwnerShapeAsync(TrackerEntryStatus.Reading);
        await using var _ = h;

        var first = await admin.PostAsJsonAsync("/api/v1/trackers/mal/entries/batch",
            new { action = "VolumesFromChapters", seriesIds = new[] { series.RecordPublicId } });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync()))
            Assert.Equal(1, json.RootElement.GetProperty("done").GetInt32());
        Assert.Equal(27, Assert.Single(h.Mal.Writes).Write.VolumesRead);

        var again = await admin.PostAsJsonAsync("/api/v1/trackers/mal/entries/batch",
            new { action = "VolumesFromChapters", seriesIds = new[] { series.RecordPublicId } });
        using (var json = JsonDocument.Parse(await again.Content.ReadAsStringAsync()))
        {
            Assert.Equal(0, json.RootElement.GetProperty("done").GetInt32());
            Assert.Equal("VolumesUpToDate", Assert.Single(json.RootElement.GetProperty("skips").EnumerateArray()).GetProperty("reason").GetString());
        }
        Assert.Single(h.Mal.Writes);
    }
}
