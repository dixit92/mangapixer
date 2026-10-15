namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Owner answer 2 (1.43.0, 2026-10-15): "Set progress from &lt;tracker&gt; to MangaPixer" ALSO marks archives in the user's Private libraries,
/// below the user's sync-off switch and of doujinshi - every library the user can open, marks always the caller's own - while the
/// outbound rules stay exactly as they are (nothing from those archives is ever sent). FAKE tracker clients only.
/// </summary>
public sealed class TrackerOwnerAnswersTwoTests
{
    private static async Task<(TrackerTestHarness H, HttpClient Admin, long UserId)> MangaUpdatesAsync()
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

    /// <summary>The golden table: which archives the "from tracker" scope holds, against what the outbound sync counts.</summary>
    [Fact]
    public async Task TheFromTrackerScope_HoldsPrivateSyncOffAndDoujinshiArchives_TheOutboundProgressCountsNoneOfThem()
    {
        var (h, admin, userId) = await MangaUpdatesAsync();
        await using var _ = h;
        var inPrivate = await h.SeedSeriesAsync(3);
        var switchedOff = await h.SeedSeriesAsync(3);
        var doujinshi = await h.SeedSeriesAsync(3, format: MetadataFormat.Doujinshi);
        var plain = await h.SeedSeriesAsync(3);
        await admin.PutAsJsonAsync("/api/v1/reading/private-libraries", new SetPrivateLibrariesRequest { LibraryIds = [inPrivate.LibraryPublicId] });
        await admin.PutAsJsonAsync("/api/v1/trackers/sync-overrides", new UpdateTrackerNodeOverridesRequest { NodeIds = [switchedOff.FolderPublicId], Enabled = false });
        await h.DbAsync(async db =>
        {
            foreach (var s in new[] { inPrivate, switchedOff, doujinshi, plain })
                db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = s.Archives[0].Id, MarkedAt = DateTimeOffset.UtcNow, Source = "tracker" });
            return await db.SaveChangesAsync();
        });

        using var scope = h.Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<TrackerSeriesResolver>();
        foreach (var (series, outbound) in new[] { (inPrivate, false), (switchedOff, false), (doujinshi, false), (plain, true) })
        {
            var (archives, _) = await resolver.ArchivesAsync(userId, series.RecordId, CancellationToken.None);
            Assert.Equal(series.Archives.Select(a => a.Id).Order(), archives.Select(a => a.NodeId).Order()); // in the "from tracker" scope
            var progress = await resolver.ProgressAsync(userId, series.RecordId, CancellationToken.None);
            Assert.Equal(outbound ? 1 : null, progress.Numbers.All.Chapters); // what outbound sync may count: unchanged
        }
    }

    [Fact]
    public async Task FromTracker_MarksInAPrivateLibrary_AndOutboundStaysSilent()
    {
        var (h, admin, userId) = await MangaUpdatesAsync();
        await using var _ = h;
        var series = await h.SeedSeriesAsync(6);
        await admin.PutAsJsonAsync("/api/v1/reading/private-libraries", new SetPrivateLibrariesRequest { LibraryIds = [series.LibraryPublicId] });
        h.MangaUpdates.Entries[series.MangaUpdatesId] = new TrackerEntry(TrackerEntryStatus.Reading, 4, null, null, null, false, null, null);

        var result = await FromTrackerAsync(admin, series);
        await h.IdleAsync();

        Assert.Equal((4, 0), (result.Marked, result.AlreadyRead));
        Assert.Single(h.MangaUpdates.Reads); // the one read of the click
        Assert.Empty(h.MangaUpdates.Writes);

        // Outbound is unchanged: reading on in the Private library sends nothing (no read, no write).
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/v1/reading/{series.Archives[5].PublicId}/read", null)).StatusCode);
        await h.IdleAsync();
        Assert.Single(h.MangaUpdates.Reads);
        Assert.Empty(h.MangaUpdates.Writes);
        Assert.All(await h.DbAsync(db => db.ReadMarks.AsNoTracking().ToListAsync()), m => Assert.Equal(userId, m.UserId)); // only the caller's
    }

    [Fact]
    public async Task FromTracker_MarksBelowASyncOffSwitch_AndForDoujinshi_AndOutboundStaysSilent()
    {
        var (h, admin, _) = await MangaUpdatesAsync();
        await using var __ = h;
        var switchedOff = await h.SeedSeriesAsync(5);
        var doujinshi = await h.SeedSeriesAsync(5, format: MetadataFormat.Doujinshi);
        await admin.PutAsJsonAsync("/api/v1/trackers/sync-overrides", new UpdateTrackerNodeOverridesRequest { NodeIds = [switchedOff.FolderPublicId], Enabled = false });
        h.MangaUpdates.Entries[switchedOff.MangaUpdatesId] = new TrackerEntry(TrackerEntryStatus.Reading, 3, null, null, null, false, null, null);
        h.MangaUpdates.Entries[doujinshi.MangaUpdatesId] = new TrackerEntry(TrackerEntryStatus.Reading, 2, null, null, null, false, null, null);

        Assert.Equal(3, (await FromTrackerAsync(admin, switchedOff)).Marked);
        Assert.Equal(2, (await FromTrackerAsync(admin, doujinshi)).Marked);
        await h.IdleAsync();
        Assert.Equal(2, h.MangaUpdates.Reads.Count);

        foreach (var s in new[] { switchedOff, doujinshi })
            await admin.PutAsync($"/api/v1/reading/{s.Archives[4].PublicId}/read", null);
        await h.IdleAsync();
        Assert.Equal(2, h.MangaUpdates.Reads.Count);
        Assert.Empty(h.MangaUpdates.Writes);
    }

    [Fact]
    public async Task FromTracker_StillStopsAtTheLibrariesTheUserCanOpen_AndMarksOnlyTheCallers()
    {
        var (h, admin, adminId) = await MangaUpdatesAsync();
        await using var _ = h;
        var series = await h.SeedSeriesAsync(2);
        var hidden = await h.SeedSeriesAsync(2);
        await h.DbAsync(async db =>
        {
            (await db.NodeSeriesLinks.SingleAsync(l => l.NodeId == hidden.FolderId)).RecordId = series.RecordId;
            return await db.SaveChangesAsync();
        });
        await h.Factory.CreateUserAsync("reader", "Reader-Pass-12345", isAdmin: false);
        var readerId = await h.UserIdAsync("reader");
        await h.DbAsync(async db =>
        {
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = readerId, LibraryId = series.LibraryId, GrantedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        // The admin made the shared library one of THEIR Private libraries: it changes nothing for the reader.
        await admin.PutAsJsonAsync("/api/v1/reading/private-libraries", new SetPrivateLibrariesRequest { LibraryIds = [series.LibraryPublicId] });
        var reader = await h.Factory.LoginAsync("reader", "Reader-Pass-12345");
        await h.ConnectMangaUpdatesAsync(reader);
        h.MangaUpdates.Entries[series.MangaUpdatesId] = new TrackerEntry(TrackerEntryStatus.Reading, 2, null, null, null, false, null, null);

        Assert.Equal(2, (await FromTrackerAsync(reader, series)).Marked);

        var marks = await h.DbAsync(db => db.ReadMarks.AsNoTracking().ToListAsync());
        Assert.All(marks, m => Assert.Equal(readerId, m.UserId));
        Assert.Equal(series.Archives.Select(a => a.Id).Order(), marks.Select(m => m.ItemId).Order());
        Assert.DoesNotContain(marks, m => m.UserId == adminId);
    }
}
