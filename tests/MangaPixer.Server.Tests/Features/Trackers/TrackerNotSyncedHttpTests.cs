namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Trackers;
using Xunit;

/// <summary>
/// 1.44.0 (owner, 2026-10-17: "why does it say nothing read? It is read"): the series popover's summary says WHY reading in a folder is not
/// synced - a Private library of the caller, a doujinshi record (both without the caller's sync switch on), or the caller's switch off -
/// instead of leaving "nothing read" unexplained; null when it syncs. Display only: no tracker request. FAKE tracker clients.
/// </summary>
public sealed class TrackerNotSyncedHttpTests
{
    private static async Task<string?> NotSyncedAsync(HttpClient client, TrackerSeriesSeed series)
    {
        var response = await client.GetAsync($"/api/v1/trackers/nodes/{series.FolderPublicId}/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("notSynced", out var reason), "the summary has no notSynced");
        return reason.ValueKind == JsonValueKind.Null ? null : reason.GetString();
    }

    [Fact]
    public async Task TheSummary_SaysWhyAFolderIsNotSynced_AndNullOnceItIs()
    {
        var h = new TrackerTestHarness();
        await using var _ = h;
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        await h.ConnectMangaUpdatesAsync(admin);
        var plain = await h.SeedSeriesAsync(2);
        var inPrivate = await h.SeedSeriesAsync(2);
        var doujinshi = await h.SeedSeriesAsync(2, format: MetadataFormat.Doujinshi);
        var switchedOff = await h.SeedSeriesAsync(2);
        Assert.True((await admin.PutAsJsonAsync("/api/v1/reading/private-libraries",
            new SetPrivateLibrariesRequest { LibraryIds = [inPrivate.LibraryPublicId] })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/trackers/sync-overrides",
            new UpdateTrackerNodeOverridesRequest { NodeIds = [switchedOff.FolderPublicId], Enabled = false })).StatusCode);
        var requests = h.MangaUpdates.Reads.Count + h.MangaUpdates.Writes.Count;

        Assert.Null(await NotSyncedAsync(admin, plain));
        Assert.Equal("PrivateLibrary", await NotSyncedAsync(admin, inPrivate));
        Assert.Equal("Doujinshi", await NotSyncedAsync(admin, doujinshi));
        Assert.Equal("SwitchedOff", await NotSyncedAsync(admin, switchedOff));

        // "Turn sync on" (the popover's action is the same switch as Select -> Sync -> On): the Private library's folder now syncs.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/trackers/sync-overrides",
            new UpdateTrackerNodeOverridesRequest { NodeIds = [inPrivate.FolderPublicId], Enabled = true })).StatusCode);
        Assert.Null(await NotSyncedAsync(admin, inPrivate));
        Assert.Equal(requests, h.MangaUpdates.Reads.Count + h.MangaUpdates.Writes.Count); // display only
    }
}
