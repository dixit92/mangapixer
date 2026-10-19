namespace com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>What one poll did (counts only); <see cref="StoppedCode"/> null = every page was read.</summary>
public sealed record ReleasesFeedResult(int Requests, int Releases, int Matched, string? StoppedCode);

/// <summary>
/// MangaUpdates' releases feed (1.45.0, lane R; MangaList's recommendation 2): every <see cref="NewChapterChecks.FeedInterval"/> the day's
/// releases list is read (<see cref="MangaUpdatesReleasesFeed"/>: only the page number and the fixed <c>include_metadata=true</c>), through
/// the gateway with the automatic gate. The releases are intersected HERE with the linked MangaUpdates records; a linked record with a
/// release that states a chapter above its stored latest chapter (or no chapter), and not fetched in the last
/// <see cref="NewChapterChecks.FeedInterval"/>, is made due now (<c>metadata_refresh_nudges</c>) - the regular id-only refresh then reads
/// its <c>latest_chapter</c>. Nothing from the feed is stored but those record ids; never a title. Logs counts only.
/// </summary>
public sealed class ReleasesFeedPoll(
    MangaPixerDbContext db, MetadataGateway gateway, MangaUpdatesReleasesFeed feed, MetadataAutoMatchService autoMatch, TimeProvider time,
    ILogger<ReleasesFeedPoll> logger)
{
    private static readonly int[] Linked = [(int)SeriesLinkState.Confirmed, (int)SeriesLinkState.Auto];

    public async Task<bool> IsDueAsync(CancellationToken ct = default)
    {
        var state = await NewChapterChecks.PeekAsync(db, ct);
        return NewChapterChecks.IsDue(state?.FeedAt, state?.FeedOutcome, NewChapterChecks.FeedInterval, time.GetUtcNow());
    }

    /// <summary>One poll. Null when the automatic gate for MangaUpdates is closed (nothing sent, nothing recorded).</summary>
    public async Task<ReleasesFeedResult?> RunAsync(CancellationToken ct = default)
    {
        // The refresh needs no matcher, but everything else of the gate (as MetadataRefreshService).
        if (await autoMatch.CheckGlobalGateAsync(ct) is { } wait && wait.Code != "matcher_unavailable")
            return null;
        var started = time.GetUtcNow();
        var mangaUpdates = MetadataProviderAllowlist.MangaUpdates;
        var linked = await (
            from r in db.MetadataRecords.AsNoTracking()
            where r.Provider == mangaUpdates && r.FetchState != 1
            join l in db.NodeSeriesLinks.AsNoTracking() on r.Id equals l.RecordId
            where Linked.Contains(l.State)
            join lib in db.Libraries.AsNoTracking() on l.LibraryId equals lib.Id
            where lib.MetadataEnabled
            select new { r.Id, r.ExternalId, r.LatestChapter, r.FetchedAt, l.LibraryId })
            .ToListAsync(ct);
        var call = MetadataCallContext.Automatic();
        string? stopped = null;
        var releases = new List<MangaUpdatesRelease>();
        if (linked.Count > 0)
        {
            var library = linked.Min(l => l.LibraryId);
            for (var page = 1; page <= MangaUpdatesReleasesFeed.MaxPages; page++)
            {
                MangaUpdatesReleasesPage answer;
                try
                {
                    var at = page;
                    answer = await gateway.DetailCallAsync(mangaUpdates, "releases", library, c => feed.PageAsync(at, c), call, ct);
                }
                catch (MetadataGatewayException ex)
                {
                    stopped = ex.Code;
                    break;
                }
                releases.AddRange(answer.Releases);
                if (!answer.HasMore(page))
                    break;
            }
        }

        // The highest chapter of each series' releases (null when one of them states none: always worth a look).
        var bySeries = releases.GroupBy(r => r.SeriesId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Any(r => r.Chapter is null) ? null : g.Max(r => r.Chapter), StringComparer.Ordinal);
        var now = time.GetUtcNow();
        var nudged = new HashSet<long>(await db.MetadataRefreshNudges.AsNoTracking().Select(n => n.RecordId).ToListAsync(ct));
        var matched = 0;
        foreach (var record in linked.GroupBy(l => l.Id).Select(g => g.First()))
        {
            if (!bySeries.TryGetValue(record.ExternalId, out var chapter) || nudged.Contains(record.Id))
                continue;
            if (now - record.FetchedAt < NewChapterChecks.FeedInterval)
                continue; // Read moments ago; MangaUpdates' own record may simply not show the release yet.
            if (chapter is { } c && record.LatestChapter is { } latest && c <= latest)
                continue; // Nothing above what is stored.
            db.MetadataRefreshNudges.Add(new MetadataRefreshNudgeEntity { RecordId = record.Id, At = now, Reason = (int)MetadataRefreshNudgeReason.ReleasesFeed });
            nudged.Add(record.Id);
            matched++;
        }
        var state = await NewChapterChecks.StateAsync(db, ct);
        state.FeedAt = started;
        state.FeedOutcome = stopped ?? "ok";
        state.FeedRequests = call.RequestsSent;
        state.FeedMatched = matched;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(LogEvents.Metadata.ReleasesFeedPoll,
            "MangaUpdates releases feed: {Releases} releases, {Matched} linked records due now, {Requests} requests; {Outcome}",
            releases.Count, matched, call.RequestsSent, stopped ?? "ok");
        return new ReleasesFeedResult(call.RequestsSent, releases.Count, matched, stopped);
    }
}
