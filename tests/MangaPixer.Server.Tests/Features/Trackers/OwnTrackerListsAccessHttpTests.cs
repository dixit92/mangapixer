namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.48.0 lane A2 (owner answer L2): the caller's own tracker lists - <c>GET trackers/{slug}/entries</c>, the Tracking page's
/// <c>series</c> and <c>series/ids</c>, and the account menu's attention badge - list only series the caller can still OPEN, and under
/// Incognito leave out the series of the caller's Private libraries (the visible libraries, as browse). The stored entries are never
/// deleted: a series whose library is granted again (or Incognito turned off) is listed again with its stored state. The chip counts
/// and the badge always count the same rows as the lists. FAKE tracker clients only - no request to any real tracker.
/// </summary>
public sealed class OwnTrackerListsAccessHttpTests
{
    private const string Base = "/api/v1/trackers/" + TrackerSlugs.MangaUpdates;

    [Fact]
    public async Task TheOwnLists_FollowTheGrants_AndIncognito_TheStoredEntriesStay()
    {
        await using var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        var plain = await h.SeedSeriesAsync(chapters: 3);
        var revoked = await h.SeedSeriesAsync(chapters: 3);
        var mine = await h.SeedSeriesAsync(chapters: 3);
        var reader = await AccessTestUsers.ReaderAsync(h.Factory, admin, "otlreader",
            [plain.LibraryId, revoked.LibraryId, mine.LibraryId], [mine.LibraryId]);
        await h.ConnectMangaUpdatesAsync(reader.Client);
        // A held jump on each series: every one needs the reader (the Held chip, the attention badge).
        await h.DbAsync(async db =>
        {
            foreach (var series in new[] { plain, revoked, mine })
            {
                db.TrackerEntries.Add(new TrackerEntryEntity
                {
                    UserId = reader.UserId,
                    Tracker = TrackerSlugs.MangaUpdates,
                    RecordId = series.RecordId,
                    State = (int)TrackerEntryState.Held,
                    RemoteId = series.MangaUpdatesId,
                    LocalChapters = 1,
                    HeldChapters = 3,
                    RemoteChapters = 1,
                    RemoteSeenAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            return await db.SaveChangesAsync();
        });

        await AssertListsAsync(reader.Client, plain, revoked, mine);

        // The grant of one library is revoked: its series drops out of every list (and the counts), its entry stays stored.
        await h.DbAsync(async db =>
            await db.LibraryGrants.Where(g => g.UserId == reader.UserId && g.LibraryId == revoked.LibraryId).ExecuteDeleteAsync());
        await AssertListsAsync(reader.Client, plain, mine);
        var kept = await h.EntryAsync(reader.UserId, TrackerSlugs.MangaUpdates, revoked.RecordId);
        Assert.NotNull(kept);
        Assert.Equal((int)TrackerEntryState.Held, kept.State);
        Assert.Equal(3, kept.HeldChapters);

        // Under Incognito the Private library's series is left out too; without Incognito it is back.
        await AssertListsAsync(reader.Incognito, plain);
        await AssertListsAsync(reader.Client, plain, mine);

        // The grant comes back: the series is listed again, with its stored state (nothing was lost).
        await h.DbAsync(async db =>
        {
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = reader.UserId, LibraryId = revoked.LibraryId, GrantedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        await AssertListsAsync(reader.Client, plain, revoked, mine);
        var back = (await reader.Client.GetFromJsonAsync<List<TrackerEntryDto>>(Base + "/entries", TestJson.Web))!
            .Single(e => e.SeriesId == revoked.RecordPublicId);
        Assert.Equal(TrackerEntryState.Held, back.State);
        Assert.Equal(3, back.HeldChapters);
        Assert.Equal(revoked.FolderPublicId, back.NodeId);
    }

    /// <summary>Every own list names exactly <paramref name="expected"/>, and the chip counts and the badge count the same rows.</summary>
    private static async Task AssertListsAsync(HttpClient client, params TrackerSeriesSeed[] expected)
    {
        var ids = expected.Select(s => s.RecordPublicId).Order(StringComparer.Ordinal).ToList();

        var entries = (await client.GetFromJsonAsync<List<TrackerEntryDto>>(Base + "/entries", TestJson.Web))!;
        Assert.Equal(ids, entries.Select(e => e.SeriesId).Order(StringComparer.Ordinal).ToList());
        Assert.All(entries, e => Assert.Contains(e.NodeId, expected.Select(s => s.FolderPublicId)));

        var page = (await client.GetFromJsonAsync<TrackerSeriesPageDto>(Base + "/series", TestJson.Web))!;
        Assert.Equal(ids, page.Items.Select(r => r.SeriesId).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(expected.Length, page.TotalCount);
        Assert.Equal(expected.Length, page.Counts.All);
        Assert.Equal(expected.Length, page.Counts.Held);
        Assert.Equal(expected.Length, page.Counts.Attention);

        var held = (await client.GetFromJsonAsync<TrackerSeriesPageDto>(Base + "/series?filter=Attention", TestJson.Web))!;
        Assert.Equal(ids, held.Items.Select(r => r.SeriesId).Order(StringComparer.Ordinal).ToList());

        var selectable = (await client.GetFromJsonAsync<TrackerSelectableSeriesListDto>(Base + "/series/ids", TestJson.Web))!;
        Assert.Equal(ids, selectable.Items.Select(r => r.SeriesId).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(expected.Length, selectable.TotalCount);

        var attention = (await client.GetFromJsonAsync<TrackerAttentionDto>("/api/v1/trackers/attention", TestJson.Web))!;
        Assert.Equal(expected.Length, attention.Total);
        Assert.Equal(expected.Length, attention.Trackers.Single(t => t.Tracker == TrackerSlugs.MangaUpdates).Count);
    }
}
