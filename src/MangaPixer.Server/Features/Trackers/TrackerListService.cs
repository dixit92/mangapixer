namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using System.Diagnostics;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The Tracking page's reads (1.43.0 tracking redesign, lane G): a tracker's series list of the caller - paged (50), filtered by chip
/// (<see cref="TrackerListBucket"/>), searched by title, with the count per chip -, the account menu's attention badge, and the series
/// header's popover. Stored state and local numbers only: nothing here makes a request to a tracker. Every row is the caller's own.
/// </summary>
public sealed class TrackerListService(
    MangaPixerDbContext db,
    TrackerSeriesResolver resolver,
    TrackerSettingsService settings,
    TimeProvider time,
    ILogger<TrackerListService> logger)
{
    /// <summary>The filter value that selects the chips needing the user (held + tracker ahead + problem).</summary>
    public const string AttentionFilter = "Attention";

    public async Task<(TrackerSeriesPageDto? Page, TrackerProblem? Problem)> PageAsync(long userId, string tracker, int? page, string? filter,
        string? search, CancellationToken ct)
    {
        if (!TrackerFacts.IsKnown(tracker))
            return (null, TrackerProblem.UnknownTracker);
        TrackerListBucket? bucket = null;
        var attention = false;
        if (!string.IsNullOrWhiteSpace(filter) && !string.Equals(filter, "All", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(filter, AttentionFilter, StringComparison.OrdinalIgnoreCase))
                attention = true;
            else if (Enum.TryParse<TrackerListBucket>(filter, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(filter, out _))
                bucket = parsed;
            else
                return (null, TrackerProblem.Bad("invalid_filter", "Unknown filter."));
        }
        var number = page ?? 1;
        if (number < 1)
            return (null, TrackerProblem.Bad("invalid_page", "Pages start at 1."));
        var text = search?.Trim();
        if (text is { Length: > TrackerFacts.MaxSearchLength })
            return (null, TrackerProblem.Bad("invalid_search", "The search text is too long."));

        var watch = Stopwatch.StartNew();
        var entries = await db.TrackerEntries.AsNoTracking().Where(e => e.UserId == userId && e.Tracker == tracker).ToListAsync(ct);
        var context = await ContextAsync(userId, tracker, entries.Select(e => e.RecordId).Distinct().ToList(), ct);
        // Only series the user can still open (a linked folder in one of their libraries): no title from a library they lost.
        var rows = entries.Where(e => context.Titles.ContainsKey(e.RecordId) && context.Nodes.ContainsKey(e.RecordId))
            .Select(e => (Entry: e, Bucket: TrackerListRules.BucketOf(FactsOf(e, context.AddStartedSeries))))
            .ToList();
        var counts = CountsOf(rows.Select(r => r.Bucket));
        IEnumerable<(TrackerEntryEntity Entry, TrackerListBucket Bucket)> selected = rows;
        if (bucket is { } only)
            selected = selected.Where(r => r.Bucket == only);
        else if (attention)
            selected = selected.Where(r => TrackerListRules.NeedsAttention(r.Bucket));
        if (!string.IsNullOrEmpty(text))
            selected = selected.Where(r => context.Titles[r.Entry.RecordId].Contains(text, StringComparison.OrdinalIgnoreCase));
        var ordered = selected
            .OrderBy(r => context.Titles[r.Entry.RecordId], StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Entry.RecordId)
            .ToList();
        var items = ordered.Skip((number - 1) * TrackerFacts.ListPageSize).Take(TrackerFacts.ListPageSize)
            .Select(r => RowOf(r.Entry, context)).ToList();
        logger.LogDebug(LogEvents.Trackers.ListRead, "Tracker list for user {UserId} on {Tracker}: {Rows} row(s), page {Page}, {ElapsedMs} ms",
            userId, tracker, ordered.Count, number, watch.ElapsedMilliseconds);
        return (new TrackerSeriesPageDto
        {
            Items = items,
            Page = number,
            PageSize = TrackerFacts.ListPageSize,
            TotalCount = ordered.Count,
            Counts = counts,
        }, null);
    }

    /// <summary>
    /// The account menu badge: per tracker the caller has an account on (and the server allows), the series that need the caller - a held
    /// jump, a tracker ahead of MangaPixer, or a failed sync (<see cref="TrackerListRules.NeedsAttention"/>).
    /// </summary>
    public async Task<TrackerAttentionDto> AttentionAsync(long userId, CancellationToken ct)
    {
        var accounts = await db.TrackerAccounts.AsNoTracking().Where(a => a.UserId == userId)
            .Select(a => new { a.Tracker, a.AddStartedSeries, a.State }).ToListAsync(ct);
        var result = new List<TrackerAttentionCountDto>();
        var held = (int)TrackerEntryState.Held;
        var ahead = (int)TrackerEntryState.TrackerAhead;
        var lower = (int)TrackerEntryState.LowerHere;
        var error = (int)TrackerEntryState.Error;
        foreach (var account in accounts.Where(a => a.State != (int)TrackerAccountState.DisconnectedByAdmin).OrderBy(a => a.Tracker, StringComparer.Ordinal))
        {
            if (!await settings.IsAllowedAsync(account.Tracker, ct))
                continue;
            // Only the rows that can be in an attention chip are read (an indexed lookup by user + tracker).
            var candidates = await db.TrackerEntries.AsNoTracking()
                .Where(e => e.UserId == userId && e.Tracker == account.Tracker && !e.DontSync
                    && (e.State == held || e.State == ahead || e.State == lower || e.State == error || e.HeldChapters != null || e.HeldVolumes != null))
                .ToListAsync(ct);
            var visible = await resolver.FirstNodesAsync(userId, candidates.Select(e => e.RecordId).Distinct().ToList(), ct);
            var count = candidates.Count(e => visible.ContainsKey(e.RecordId)
                && TrackerListRules.NeedsAttention(TrackerListRules.BucketOf(FactsOf(e, account.AddStartedSeries))));
            result.Add(new TrackerAttentionCountDto { Tracker = account.Tracker, Count = count });
        }
        return new TrackerAttentionDto { Total = result.Sum(r => r.Count), Trackers = result };
    }

    /// <summary>
    /// The series header popover: the series the node belongs to (its nearest Confirmed / Auto link, the node in a library the caller can
    /// open) and one row per tracker the caller has an account on that the server allows - the stored entry, or, for a series not synced yet,
    /// its local numbers and tracker id (no request; nothing is stored).
    /// </summary>
    public async Task<(TrackerSeriesSummaryDto? Summary, TrackerProblem? Problem)> SummaryAsync(long userId, string nodePublicId, CancellationToken ct)
    {
        var node = await resolver.SeriesOfNodeAsync(userId, nodePublicId, ct);
        if (node is null)
            return (null, TrackerProblem.NotFound("unknown_node", "There is no such folder in your libraries."));
        if (node.Value.RecordId is not { } recordId)
            return (new TrackerSeriesSummaryDto { SeriesId = null, Rows = [] }, null);

        var accounts = await db.TrackerAccounts.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct);
        var trackers = new List<TrackerAccountEntity>();
        foreach (var account in accounts.Where(a => a.State != (int)TrackerAccountState.DisconnectedByAdmin).OrderBy(a => a.Tracker, StringComparer.Ordinal))
        {
            if (await settings.IsAllowedAsync(account.Tracker, ct))
                trackers.Add(account);
        }
        var seriesId = await db.MetadataRecords.AsNoTracking().Where(r => r.Id == recordId).Select(r => r.PublicId).FirstAsync(ct);
        if (trackers.Count == 0)
            return (new TrackerSeriesSummaryDto { SeriesId = seriesId, Rows = [] }, null);

        var stored = await db.TrackerEntries.AsNoTracking().Where(e => e.UserId == userId && e.RecordId == recordId).ToListAsync(ct);
        TrackerSeriesProgress? progress = null;
        var rows = new List<TrackerSeriesRowDto>();
        foreach (var account in trackers)
        {
            var entry = stored.FirstOrDefault(e => e.Tracker == account.Tracker);
            if (entry is null)
            {
                progress ??= await resolver.ProgressAsync(userId, recordId, ct);
                var remoteId = await resolver.RemoteIdAsync(recordId, account.Tracker, ct);
                entry = new TrackerEntryEntity
                {
                    UserId = userId,
                    Tracker = account.Tracker,
                    RecordId = recordId,
                    RemoteId = remoteId?.Id,
                    RemoteIdSource = (int?)remoteId?.Source,
                    State = (int)(remoteId is null ? TrackerEntryState.NoId : TrackerEntryState.Pending),
                    LocalChapters = progress.Numbers.Automatic.Chapters,
                    LocalVolumes = progress.Numbers.Automatic.Volumes,
                    AllChapters = progress.Numbers.All.Chapters,
                    AllVolumes = progress.Numbers.All.Volumes,
                    AutoStartedOn = DayOf(progress.StartedOn),
                    UpdatedAt = time.GetUtcNow(),
                };
            }
            var context = await ContextAsync(userId, account.Tracker, [recordId], ct);
            if (context.Titles.ContainsKey(recordId))
                rows.Add(RowOf(entry, context));
        }
        return (new TrackerSeriesSummaryDto { SeriesId = seriesId, Rows = rows }, null);
    }

    /// <summary>The rows of stored entries (for the per-series actions' answers).</summary>
    public async Task<IReadOnlyList<TrackerSeriesRowDto>> RowsAsync(long userId, string tracker, IReadOnlyList<TrackerEntryEntity> entries,
        CancellationToken ct)
    {
        var context = await ContextAsync(userId, tracker, entries.Select(e => e.RecordId).Distinct().ToList(), ct);
        return entries.Where(e => context.Titles.ContainsKey(e.RecordId)).Select(e => RowOf(e, context)).ToList();
    }

    /// <summary>What a row needs beyond the entry: the titles, the first folder the caller can open, the record totals, the user setting.</summary>
    private sealed record RowContext(
        IReadOnlyDictionary<long, string> Titles,
        IReadOnlyDictionary<long, string> SeriesIds,
        IReadOnlyDictionary<long, string> Nodes,
        IReadOnlyDictionary<long, (int? Chapters, int? Volumes)> RecordTotals,
        bool AddStartedSeries);

    private async Task<RowContext> ContextAsync(long userId, string tracker, IReadOnlyList<long> recordIds, CancellationToken ct)
    {
        var titles = new Dictionary<long, string>();
        var seriesIds = new Dictionary<long, string>();
        var totals = new Dictionary<long, (int?, int?)>();
        foreach (var chunk in recordIds.Chunk(500))
        {
            foreach (var r in await db.MetadataRecords.AsNoTracking().Where(r => chunk.Contains(r.Id))
                         .Select(r => new { r.Id, r.PublicId, r.Title, r.LatestChapter, r.OriginVolumes }).ToListAsync(ct))
            {
                titles[r.Id] = r.Title;
                seriesIds[r.Id] = r.PublicId;
                totals[r.Id] = (r.LatestChapter is { } c && c >= 1 && c <= 100_000 ? (int)Math.Floor(c) : null, r.OriginVolumes is > 0 ? r.OriginVolumes : null);
            }
        }
        var nodes = await resolver.FirstNodesAsync(userId, recordIds, ct);
        var addStarted = await db.TrackerAccounts.AsNoTracking().Where(a => a.UserId == userId && a.Tracker == tracker)
            .Select(a => (bool?)a.AddStartedSeries).FirstOrDefaultAsync(ct) ?? true;
        return new RowContext(titles, seriesIds, nodes, totals, addStarted);
    }

    /// <summary>The list rules' view of a stored entry.</summary>
    internal static TrackerRowFacts FactsOf(TrackerEntryEntity e, bool addStartedSeries)
    {
        var automatic = new TrackerNumbers(e.LocalChapters, e.LocalVolumes);
        var all = new TrackerNumbers(Higher(e.AllChapters, e.LocalChapters), Higher(e.AllVolumes, e.LocalVolumes));
        all = new TrackerNumbers(Higher(all.Chapters, e.HeldChapters), Higher(all.Volumes, e.HeldVolumes));
        var state = (TrackerEntryState)e.State;
        return new TrackerRowFacts(
            state,
            e.DontSync,
            HasRemoteId: e.RemoteId is not null || state == TrackerEntryState.Pending,
            RemoteSeen: e.RemoteSeenAt is not null,
            // 1.43.1: "on none of your lists" only for a series whose tracker id is known - without one nobody could look (an entry stored
            // that way by 1.43.0's whole-list read reads as "No <tracker> id" again).
            e.RemoteMissing && e.RemoteId is not null,
            (TrackerEntryStatus?)e.RemoteStatus,
            automatic,
            all,
            new TrackerNumbers(e.HeldChapters, e.HeldVolumes),
            new TrackerNumbers(e.RemoteChapters, e.RemoteVolumes),
            addStartedSeries);
    }

    private static TrackerSeriesRowDto RowOf(TrackerEntryEntity e, RowContext context)
    {
        var facts = FactsOf(e, context.AddStartedSeries);
        var fromBlock = TrackerListRules.FromTrackerBlock(facts);
        var toBlock = TrackerListRules.ToTrackerBlock(facts);
        var (recordChapters, recordVolumes) = context.RecordTotals.GetValueOrDefault(e.RecordId);
        return new TrackerSeriesRowDto
        {
            SeriesId = context.SeriesIds[e.RecordId],
            Title = context.Titles[e.RecordId],
            NodeId = context.Nodes.GetValueOrDefault(e.RecordId),
            Tracker = e.Tracker,
            RemoteId = e.RemoteId,
            RemoteIdSource = (TrackerIdSource?)e.RemoteIdSource,
            State = (TrackerEntryState)e.State,
            Bucket = TrackerListRules.BucketOf(facts),
            ErrorCode = e.ErrorCode,
            DontSync = e.DontSync,
            Chapters = e.LocalChapters,
            Volumes = e.LocalVolumes,
            AllChapters = facts.All.Chapters,
            AllVolumes = facts.All.Volumes,
            HeldChapters = e.HeldChapters,
            HeldVolumes = e.HeldVolumes,
            TotalChapters = e.RemoteTotalChapters ?? recordChapters,
            TotalVolumes = e.RemoteTotalVolumes ?? recordVolumes,
            RemoteChapters = e.RemoteChapters,
            RemoteVolumes = e.RemoteVolumes,
            RemoteStatus = (TrackerEntryStatus?)e.RemoteStatus,
            RemoteMissing = e.RemoteMissing && e.RemoteId is not null,
            Dates = DatesOf(e),
            FromTracker = new TrackerActionDto { Possible = fromBlock == TrackerActionBlock.None, Block = fromBlock },
            ToTracker = new TrackerActionDto { Possible = toBlock == TrackerActionBlock.None, Block = toBlock },
            LastSyncAt = e.RemoteSeenAt,
            LastSentAt = e.LastSentAt,
            UpdatedAt = e.UpdatedAt,
        };
    }

    internal static TrackerDatesDto DatesOf(TrackerEntryEntity e)
    {
        if (!TrackerFacts.SupportsDates(e.Tracker))
            return new TrackerDatesDto { Supported = false, StartedOnOverridden = false, FinishedOnOverridden = false, Pending = false };
        var startOverride = ParseDay(e.StartedOnOverride);
        var finishOverride = ParseDay(e.FinishedOnOverride);
        return new TrackerDatesDto
        {
            Supported = true,
            // The user's own date, else what the tracker keeps, else the automatic date MangaPixer would send (owner review 2026-10-15:
            // never MangaPixer's first mark beside the tracker's own finish).
            StartedOn = startOverride ?? ParseDay(e.RemoteStartedOn) ?? ParseDay(e.AutoStartedOn),
            StartedOnOverridden = startOverride is not null,
            FinishedOn = finishOverride ?? ParseDay(e.RemoteFinishedOn) ?? ParseDay(e.AutoFinishedOn),
            FinishedOnOverridden = finishOverride is not null,
            RemoteStartedOn = ParseDay(e.RemoteStartedOn),
            RemoteFinishedOn = ParseDay(e.RemoteFinishedOn),
            Pending = e.DatesPending,
        };
    }

    private static TrackerListCountsDto CountsOf(IEnumerable<TrackerListBucket> buckets)
    {
        var list = buckets.ToList();
        int Of(TrackerListBucket b) => list.Count(x => x == b);
        return new TrackerListCountsDto
        {
            All = list.Count,
            InSync = Of(TrackerListBucket.InSync),
            MangaPixerAhead = Of(TrackerListBucket.MangaPixerAhead),
            TrackerAhead = Of(TrackerListBucket.TrackerAhead),
            LeftAsIs = Of(TrackerListBucket.LeftAsIs),
            Held = Of(TrackerListBucket.Held),
            NoId = Of(TrackerListBucket.NoId),
            NotSynced = Of(TrackerListBucket.NotSynced),
            Problem = Of(TrackerListBucket.Problem),
            Unchecked = Of(TrackerListBucket.Unchecked),
            Attention = list.Count(TrackerListRules.NeedsAttention),
        };
    }

    /// <summary>A stored UTC day ("yyyy-MM-dd"), or null.</summary>
    public static DateOnly? ParseDay(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    public static string? DayOf(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static int? Higher(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
