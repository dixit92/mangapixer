namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A user's tracker list (1.43.0): the stored entries with their states, the per-series actions (Sync now, Send a held jump, "Set the
/// tracker to N", Don't sync), the first-connect preview (reads only - no automatic backfill), Send all, and the user's sync switches on
/// folders / series (owner Q3). Every action is the user's own and touches only the user's own rows.
/// </summary>
public sealed class TrackerEntriesService(
    MangaPixerDbContext db,
    TrackerClients clients,
    TrackerListCheckService listCheck,
    TrackerSyncService sync,
    TrackerSeriesResolver resolver,
    TrackerSettingsService settings,
    LibraryAuthorizationService libraries,
    TimeProvider time,
    ILogger<TrackerEntriesService> logger)
{
    public async Task<(IReadOnlyList<TrackerEntryDto>? Entries, TrackerProblem? Problem)> ListAsync(long userId, string tracker, CancellationToken ct)
    {
        if (!TrackerFacts.IsKnown(tracker))
            return (null, TrackerProblem.UnknownTracker);
        var rows = await db.TrackerEntries.AsNoTracking().Where(e => e.UserId == userId && e.Tracker == tracker).ToListAsync(ct);
        return (await ToDtosAsync(userId, rows, ct), null);
    }

    /// <summary>One per-series action. <paramref name="setTo"/> only for <see cref="TrackerSyncMode.SetTo"/>.</summary>
    public async Task<(TrackerEntryDto? Entry, TrackerProblem? Problem)> ActAsync(long userId, string tracker, string seriesId, TrackerSyncMode mode,
        TrackerNumbers? setTo, CancellationToken ct)
    {
        var (recordId, problem) = await UsableAsync(userId, tracker, seriesId, ct);
        if (problem is not null)
            return (null, problem);
        if (mode == TrackerSyncMode.SetTo
            && (setTo is not { } m || m.IsEmpty || m.Chapters is < 0 or > 100_000 || m.Volumes is < 0 or > 100_000))
        {
            return (null, TrackerProblem.Bad("invalid_numbers", "Give a chapter or volume number from 0 to 100000."));
        }
        var entry = await sync.SyncAsync(userId, tracker, recordId, mode, TrackerSyncTrigger.User, setTo, ct);
        if (entry is null)
            return (null, TrackerProblem.Conflict("not_syncing", "Your account on this tracker cannot sync right now."));
        return ((await ToDtosAsync(userId, [entry], ct))[0], null);
    }

    public async Task<(TrackerEntryDto? Entry, TrackerProblem? Problem)> SetDontSyncAsync(long userId, string tracker, string seriesId, bool dontSync,
        CancellationToken ct)
    {
        if (!TrackerFacts.IsKnown(tracker))
            return (null, TrackerProblem.UnknownTracker);
        var recordId = await RecordIdAsync(userId, seriesId, ct);
        if (recordId is null)
            return (null, TrackerProblem.NotFound("unknown_series", "There is no such series in your libraries."));
        var now = time.GetUtcNow();
        var entry = await db.TrackerEntries.FirstOrDefaultAsync(e => e.UserId == userId && e.Tracker == tracker && e.RecordId == recordId, ct);
        if (entry is null)
        {
            entry = new TrackerEntryEntity { UserId = userId, Tracker = tracker, RecordId = recordId.Value, State = (int)TrackerEntryState.Pending };
            db.TrackerEntries.Add(entry);
        }
        entry.DontSync = dontSync;
        if (dontSync)
        {
            entry.State = (int)TrackerEntryState.DontSync;
            entry.HeldChapters = null;
            entry.HeldVolumes = null;
            await db.TrackerRetries.Where(r => r.UserId == userId && r.Tracker == tracker && r.RecordId == recordId).ExecuteDeleteAsync(ct);
        }
        else if (entry.State == (int)TrackerEntryState.DontSync)
        {
            entry.State = (int)TrackerEntryState.Pending;
        }
        entry.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return ((await ToDtosAsync(userId, [entry], ct))[0], null);
    }

    /// <summary>
    /// The first-connect preview: every series the user has an eligible mark in gets an entry; up to <see cref="TrackerFacts.PreviewBatch"/>
    /// series not read from the tracker yet are read (no write - a series MangaPixer is ahead on waits for "Send").
    /// </summary>
    public async Task<(TrackerPreviewDto? Preview, TrackerProblem? Problem)> PreviewAsync(long userId, string tracker, CancellationToken ct)
    {
        var account = await ConnectedAsync(userId, tracker, ct);
        if (account.Problem is not null)
            return (null, account.Problem);

        var markedItems = await db.ReadMarks.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.ItemId).ToListAsync(ct);
        var records = (await resolver.RecordsOfAsync(userId, markedItems, ct)).Values.Distinct().ToList();
        var seen = (await db.TrackerEntries.AsNoTracking()
                .Where(e => e.UserId == userId && e.Tracker == tracker && e.RemoteSeenAt != null).Select(e => e.RecordId).ToListAsync(ct))
            .ToHashSet();
        // 1.43.0 (lane G): every series with an eligible mark gets a row on the Tracking page now ("not checked" until it is read).
        var known = (await db.TrackerEntries.AsNoTracking().Where(e => e.UserId == userId && e.Tracker == tracker).Select(e => e.RecordId)
            .ToListAsync(ct)).ToHashSet();
        var now = time.GetUtcNow();
        foreach (var recordId in records.Where(r => !known.Contains(r)))
        {
            db.TrackerEntries.Add(new TrackerEntryEntity
            {
                UserId = userId,
                Tracker = tracker,
                RecordId = recordId,
                State = (int)TrackerEntryState.Pending,
                UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        // 1.43.0 (owner option C): a tracker that can read the whole list does so - every series here on it gets its row, no per-series reads.
        if (clients.Get(tracker) is { } client && client is ITrackerListReader { ReadsWholeList: true } reader)
        {
            var (check, problem) = await listCheck.CheckAsync(userId, tracker, client, reader, records, ct);
            if (problem is not null)
                return (null, problem);
            var listed = await db.TrackerEntries.AsNoTracking().Where(e => e.UserId == userId && e.Tracker == tracker).ToListAsync(ct);
            return (new TrackerPreviewDto
            {
                Entries = await ToDtosAsync(userId, listed, ct),
                Remaining = 0,
                CapReached = check!.CapReached,
                ListRead = !check.CapReached,
                ListEntries = check.ListEntries,
                Matched = check.Matched,
                Created = check.Created,
            }, null);
        }

        // Otherwise (MangaUpdates): the series read here, 50 per call, one read each.
        var unread = records.Where(r => !seen.Contains(r)).Order().ToList();
        var capReached = false;
        var read = 0;
        foreach (var recordId in unread.Take(TrackerFacts.PreviewBatch))
        {
            var entry = await sync.SyncAsync(userId, tracker, recordId, TrackerSyncMode.SendAll, TrackerSyncTrigger.Preview, null, ct);
            if (entry is null)
                break;
            if (entry.RemoteSeenAt is null && entry.ErrorCode == "daily_cap")
            {
                capReached = true;
                break;
            }
            read++;
        }
        logger.LogInformation(LogEvents.Trackers.PreviewRead, "Tracker preview for user {UserId} on {Tracker}: {Read} read, {Series} series",
            userId, tracker, read, records.Count);
        var rows = await db.TrackerEntries.AsNoTracking().Where(e => e.UserId == userId && e.Tracker == tracker).ToListAsync(ct);
        return (new TrackerPreviewDto
        {
            Entries = await ToDtosAsync(userId, rows, ct),
            Remaining = Math.Max(0, unread.Count - read),
            CapReached = capReached,
        }, null);
    }

    /// <summary>"Send all": every series of the user's list with a held jump, until the daily cap.</summary>
    public async Task<(TrackerSendAllResultDto? Result, TrackerProblem? Problem)> SendAllAsync(long userId, string tracker, CancellationToken ct)
    {
        var account = await ConnectedAsync(userId, tracker, ct);
        if (account.Problem is not null)
            return (null, account.Problem);
        var held = await db.TrackerEntries.AsNoTracking()
            .Where(e => e.UserId == userId && e.Tracker == tracker && !e.DontSync && (e.HeldChapters != null || e.HeldVolumes != null))
            .Select(e => new { e.RecordId, e.LastSentAt }).ToListAsync(ct);
        int sent = 0, skipped = 0, failed = 0;
        var capReached = false;
        foreach (var (recordId, sentBefore) in held.Select(h => (h.RecordId, h.LastSentAt)))
        {
            var entry = await sync.SyncAsync(userId, tracker, recordId, TrackerSyncMode.SendAll, TrackerSyncTrigger.User, null, ct);
            if (entry is null)
                break;
            if (entry.ErrorCode == "daily_cap")
            {
                capReached = true;
                break;
            }
            if (entry.State == (int)TrackerEntryState.Error)
                failed++;
            else if (entry.LastSentAt != sentBefore)
                sent++;
            else
                skipped++;
        }
        logger.LogInformation(LogEvents.Trackers.SendAll, "Tracker send-all for user {UserId} on {Tracker}: {Sent} sent, {Skipped} skipped, {Failed} failed",
            userId, tracker, sent, skipped, failed);
        return (new TrackerSendAllResultDto { Sent = sent, Skipped = skipped, Failed = failed, CapReached = capReached }, null);
    }

    public async Task<IReadOnlyList<TrackerNodeOverrideDto>> ListOverridesAsync(long userId, CancellationToken ct)
    {
        var rows = await (from o in db.TrackerNodeOverrides.AsNoTracking()
                          join n in db.CatalogNodes.AsNoTracking() on o.NodeId equals n.Id
                          join l in db.Libraries.AsNoTracking() on n.LibraryId equals l.Id
                          where o.UserId == userId
                          orderby n.SortKey
                          select new { o.Enabled, o.UpdatedAt, n.PublicId, n.DisplayName, n.LibraryId, LibraryPublicId = l.PublicId }).ToListAsync(ct);
        var accessible = (await libraries.GetAccessibleLibraryIdsAsync(userId, ct)).ToHashSet();
        return rows.Where(r => accessible.Contains(r.LibraryId)).Select(r => new TrackerNodeOverrideDto
        {
            NodeId = r.PublicId,
            Name = r.DisplayName,
            LibraryId = r.LibraryPublicId,
            Enabled = r.Enabled,
            UpdatedAt = r.UpdatedAt,
        }).ToList();
    }

    /// <summary>Sets (true / false) or clears (null) the user's switch on each node; refuses nodes the user cannot open.</summary>
    public async Task<(IReadOnlyList<TrackerNodeOverrideDto>? List, TrackerProblem? Problem)> SetOverridesAsync(long userId,
        UpdateTrackerNodeOverridesRequest request, CancellationToken ct)
    {
        var publicIds = (request.NodeIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (publicIds.Count is 0 or > 500)
            return (null, TrackerProblem.Bad("invalid_nodes", "Select 1 to 500 folders or series."));
        var accessible = (await libraries.GetAccessibleLibraryIdsAsync(userId, ct)).ToList();
        var nodes = await db.CatalogNodes.AsNoTracking()
            .Where(n => publicIds.Contains(n.PublicId) && accessible.Contains(n.LibraryId))
            .Select(n => new { n.Id, n.PublicId }).ToListAsync(ct);
        if (nodes.Count != publicIds.Count)
            return (null, TrackerProblem.NotFound("unknown_node", "A selected folder or series does not exist."));
        var ids = nodes.Select(n => n.Id).ToList();
        var existing = await db.TrackerNodeOverrides.Where(o => o.UserId == userId && ids.Contains(o.NodeId)).ToListAsync(ct);
        var now = time.GetUtcNow();
        if (request.Enabled is not { } enabled)
        {
            db.TrackerNodeOverrides.RemoveRange(existing);
        }
        else
        {
            foreach (var id in ids)
            {
                var row = existing.FirstOrDefault(o => o.NodeId == id);
                if (row is null)
                    db.TrackerNodeOverrides.Add(row = new TrackerNodeOverrideEntity { UserId = userId, NodeId = id });
                row.Enabled = enabled;
                row.UpdatedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
        return (await ListOverridesAsync(userId, ct), null);
    }

    internal async Task<(TrackerAccountEntity? Account, TrackerProblem? Problem)> ConnectedAsync(long userId, string tracker, CancellationToken ct)
    {
        if (!TrackerFacts.IsKnown(tracker))
            return (null, TrackerProblem.UnknownTracker);
        if (!await settings.IsAllowedAsync(tracker, ct))
            return (null, TrackerProblem.NotAllowed);
        var account = await db.TrackerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId && a.Tracker == tracker, ct);
        if (account is null)
            return (null, TrackerProblem.NotConnected);
        if (account.State != (int)TrackerAccountState.Ok || account.ConsentVersion != TrackerConsent.UserVersion(tracker))
            return (null, TrackerProblem.Conflict("needs_reconnect", "Connect your account again."));
        return (account, null);
    }

    internal async Task<(long RecordId, TrackerProblem? Problem)> UsableAsync(long userId, string tracker, string seriesId, CancellationToken ct)
    {
        var account = await ConnectedAsync(userId, tracker, ct);
        if (account.Problem is not null)
            return (0, account.Problem);
        var recordId = await RecordIdAsync(userId, seriesId, ct);
        return recordId is null ? (0, TrackerProblem.NotFound("unknown_series", "There is no such series in your libraries.")) : (recordId.Value, null);
    }

    /// <summary>The record behind a series id, when one of its Confirmed / Auto linked folders is in a library the user can open.</summary>
    internal async Task<long?> RecordIdAsync(long userId, string seriesId, CancellationToken ct)
    {
        var accessible = (await libraries.GetAccessibleLibraryIdsAsync(userId, ct)).ToList();
        var states = new[] { (int)Core.Metadata.SeriesLinkState.Confirmed, (int)Core.Metadata.SeriesLinkState.Auto };
        return await (from r in db.MetadataRecords.AsNoTracking()
                      join l in db.NodeSeriesLinks.AsNoTracking() on r.Id equals l.RecordId
                      join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
                      where r.PublicId == seriesId && states.Contains(l.State) && accessible.Contains(n.LibraryId)
                      select (long?)r.Id).FirstOrDefaultAsync(ct);
    }

    private async Task<IReadOnlyList<TrackerEntryDto>> ToDtosAsync(long userId, IReadOnlyList<TrackerEntryEntity> rows, CancellationToken ct)
    {
        var recordIds = rows.Select(r => r.RecordId).Distinct().ToList();
        var records = await db.MetadataRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id))
            .Select(r => new { r.Id, r.PublicId, r.Title }).ToDictionaryAsync(r => r.Id, ct);
        var accessible = (await libraries.GetAccessibleLibraryIdsAsync(userId, ct)).ToList();
        var states = new[] { (int)Core.Metadata.SeriesLinkState.Confirmed, (int)Core.Metadata.SeriesLinkState.Auto };
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var nodes = (await (from l in db.NodeSeriesLinks.AsNoTracking()
                            join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
                            where l.RecordId != null && recordIds.Contains(l.RecordId.Value) && states.Contains(l.State)
                                  && accessible.Contains(n.LibraryId) && n.Availability != tombstoned
                            orderby n.Id
                            select new { RecordId = l.RecordId!.Value, n.PublicId }).ToListAsync(ct))
            .GroupBy(n => n.RecordId).ToDictionary(g => g.Key, g => g.First().PublicId);
        return rows.Where(r => records.ContainsKey(r.RecordId))
            .OrderBy(r => records[r.RecordId].Title, StringComparer.OrdinalIgnoreCase)
            .Select(r => new TrackerEntryDto
            {
                SeriesId = records[r.RecordId].PublicId,
                Title = records[r.RecordId].Title,
                NodeId = nodes.GetValueOrDefault(r.RecordId),
                Tracker = r.Tracker,
                RemoteId = r.RemoteId,
                RemoteIdSource = (TrackerIdSource?)r.RemoteIdSource,
                State = (TrackerEntryState)r.State,
                ErrorCode = r.ErrorCode,
                DontSync = r.DontSync,
                LocalChapters = r.LocalChapters,
                LocalVolumes = r.LocalVolumes,
                HeldChapters = r.HeldChapters,
                HeldVolumes = r.HeldVolumes,
                LastSentChapters = r.LastSentChapters,
                LastSentVolumes = r.LastSentVolumes,
                LastSentAt = r.LastSentAt,
                RemoteChapters = r.RemoteChapters,
                RemoteVolumes = r.RemoteVolumes,
                RemoteStatus = (TrackerEntryStatus?)r.RemoteStatus,
                RemoteMissing = r.RemoteMissing,
                RemoteSeenAt = r.RemoteSeenAt,
                UpdatedAt = r.UpdatedAt,
            }).ToList();
    }
}
