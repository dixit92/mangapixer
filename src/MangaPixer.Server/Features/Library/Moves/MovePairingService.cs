namespace com.lifepixer.mangapixer.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Api;
using System.Diagnostics;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Moves recognised after the fact (1.31.0, owner decision 4: "destination scanned first"). The destination scan has already
/// created new nodes for the moved archives; once the source scan tombstones the old ones and the new copies are analysed,
/// this pass matches each old archive to its new copy (<see cref="MovePairing"/> + <see cref="ManifestAgreement"/>), copies
/// the old state onto the new copy where the new copy has none, records a conflict where both differ, carries the old
/// folders' rows to the new folders, and marks the old archive handled (<c>node_moves</c>) so nothing runs twice. The old
/// archive stays a tombstone (the trash purges it after the window; it is held while a conflict is open).
/// Idempotent; one indexed query when there is nothing to do. Local only; logs ids and counts.
/// 1.40.0: then carries read state onto a volume archive that replaced its chapter archives (<see cref="UpgradeCarryOverService"/>) -
/// after the pairing, so a real move wins.
/// </summary>
public sealed class MovePairingService
{
    private readonly MangaPixerDbContext _db;
    private readonly MetadataCarryOverService _carryOver;
    private readonly UpgradeCarryOverService _upgrades;
    private readonly TimeProvider _time;
    private readonly ILogger<MovePairingService> _logger;

    public MovePairingService(MangaPixerDbContext db, MetadataCarryOverService carryOver, UpgradeCarryOverService upgrades, TimeProvider time,
        ILogger<MovePairingService> logger)
    {
        _db = db;
        _carryOver = carryOver;
        _upgrades = upgrades;
        _time = time;
        _logger = logger;
    }

    public sealed record PassResult(int Paired, int Conflicts, int Waiting, int Ambiguous, int ManifestMismatch, int Busy, int FoldersCarried)
    {
        /// <summary>1.40.0: what the chapter-to-volume upgrade step of the same pass carried.</summary>
        public UpgradeCarryOverService.PassResult Upgrades { get; init; } = UpgradeCarryOverService.PassResult.None;
    }

    private sealed record OldRow(long Id, long LibraryId, long? ParentId, long ByteLength, string Signature, long ContentVersion,
        long LastSeenScanRevision, DateTimeOffset? TombstonedAt, DateTimeOffset CreatedAt);

    private sealed record NewRow(long Id, long LibraryId, long? ParentId, long ByteLength, string? Signature, long ContentVersion, DateTimeOffset CreatedAt);

    /// <summary>One pass: identical-content pairing first, then the chapter-to-volume upgrades (whose evidence excludes what was just paired).</summary>
    public async Task<PassResult> RunAsync(CancellationToken ct = default)
    {
        var paired = await PairAsync(ct);
        return paired with { Upgrades = await _upgrades.RunAsync(ct) };
    }

    private async Task<PassResult> PairAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var now = _time.GetUtcNow();
        var windowStart = await MoveEvidence.WindowStartAsync(_db, now, ct);
        var tombstoned = MoveEvidence.Tombstoned;

        var olds = (await _db.CatalogNodes.AsNoTracking()
                .Where(n => n.Kind == 1 && n.Availability == tombstoned && n.TombstonedAt != null && n.TombstonedAt >= windowStart
                    && !_db.NodeMoves.Any(m => m.FromNodeId == n.Id))
                .Join(_db.ArchiveItems.Where(a => a.ContentSignature != null), n => n.Id, a => a.NodeId,
                    (n, a) => new OldRow(n.Id, n.LibraryId, n.ParentId, a.ByteLength, a.ContentSignature!, a.ContentVersion,
                        n.LastSeenScanRevision, n.TombstonedAt, n.CreatedAt))
                .ToListAsync(ct))
            .Where(o => MoveEvidence.IsUsable(o.Signature, o.ByteLength))
            .ToList();
        if (olds.Count == 0)
            return new PassResult(0, 0, 0, 0, 0, 0, 0);

        var lastSeen = await MoveEvidence.LastSeenAtAsync(_db,
            olds.Select(o => new MoveEvidence.Sighting(o.Id, o.LibraryId, o.LastSeenScanRevision, o.TombstonedAt)).ToList(), ct);
        var news = await LoadNewSideAsync(olds, ct);
        var oldById = olds.ToDictionary(o => o.Id);
        var newById = news.ToDictionary(n => n.Id);

        var decisions = MovePairing.Decide(
            olds.Select(o => new MoveOldCandidate(o.Id, o.LibraryId, o.ByteLength, o.Signature, lastSeen[o.Id])).ToList(),
            news.Select(n => new MoveNewCandidate(n.Id, n.LibraryId, n.ByteLength, n.Signature, n.CreatedAt)).ToList());

        var running = (await MoveEvidence.RunningScanLibraryIds(_db, now).Distinct().ToListAsync(ct)).ToHashSet();
        int paired = 0, conflicts = 0, mismatch = 0, busy = 0;
        var ledger = new List<(long Library, ScanMove Move)>();
        var pairedOld = new HashSet<long>();
        var touchedFolders = new HashSet<long>();
        foreach (var decision in decisions.Where(d => d.Outcome == MovePairOutcome.Paired))
        {
            var old = oldById[decision.Old.NodeId];
            var neu = newById[decision.New!.NodeId];
            if (running.Contains(old.LibraryId) || running.Contains(neu.LibraryId))
            {
                busy++;
                continue;
            }
            var oldPages = await PagesAsync(old.Id, old.ContentVersion, ct);
            var newPages = await PagesAsync(neu.Id, neu.ContentVersion, ct);
            if (!ManifestAgreement.Agree(oldPages, newPages))
            {
                mismatch++;
                _logger.LogDebug(LogEvents.Scanning.MovePairingSkipped, "Move pairing: node {OldId} and node {NewId} share a signature but not their pages", old.Id, neu.Id);
                continue;
            }
            conflicts += await TransferAsync(old, neu, oldPages, newPages, now, ct);
            paired++;
            pairedOld.Add(old.Id);
            if (old.ParentId is { } oldParent)
                ledger.Add((neu.LibraryId, new ScanMove(neu.Id, oldParent)));
            if (neu.ParentId is { } newParent)
                touchedFolders.Add(newParent);
        }

        var folders = 0;
        foreach (var group in ledger.GroupBy(l => l.Library))
        {
            try
            {
                folders += await _carryOver.CarryAsync(group.Key, group.Select(g => g.Move).ToList(), pairedOld, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.Scanning.MovePairingFailed, "Move pairing: folder carry-over failed for library {LibraryId}: {Error}", group.Key, ex.GetType().Name);
            }
        }
        if (paired > 0)
        {
            await RecomputeRecencyAsync(touchedFolders, ct);
            var destLibraries = decisions.Where(d => d.Outcome == MovePairOutcome.Paired && pairedOld.Contains(d.Old.NodeId))
                .Select(d => d.New!.LibraryId).Distinct().ToList();
            await _db.Libraries.Where(l => destLibraries.Contains(l.Id))
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.CatalogRevision, l => l.CatalogRevision + 1), ct);
        }

        var waiting = decisions.Count(d => d.Outcome == MovePairOutcome.Waiting);
        var ambiguous = decisions.Count(d => d.Outcome == MovePairOutcome.Ambiguous);
        if (paired > 0 || waiting > 0 || ambiguous > 0 || mismatch > 0 || busy > 0)
            _logger.LogInformation(LogEvents.Scanning.MovePairingPass,
                "Move pairing: {Paired} archives paired, {Conflicts} conflicts, {Folders} folders carried, {Waiting} waiting for analysis, {Ambiguous} ambiguous, {Mismatch} manifest mismatches, {Busy} left for a running scan, in {ElapsedMs} ms",
                paired, conflicts, folders, waiting, ambiguous, mismatch, busy, watch.ElapsedMilliseconds);
        return new PassResult(paired, conflicts, waiting, ambiguous, mismatch, busy, folders);
    }

    /// <summary>
    /// Live archives that may be a moved copy: analysed ones carrying one of the old signatures, and same-size ones still
    /// waiting for analysis. Never one that already received a move.
    /// </summary>
    private async Task<List<NewRow>> LoadNewSideAsync(List<OldRow> olds, CancellationToken ct)
    {
        var tombstoned = MoveEvidence.Tombstoned;
        var result = new List<NewRow>();
        var live = _db.CatalogNodes.Where(n => n.Kind == 1 && n.Availability != tombstoned && !_db.NodeMoves.Any(m => m.ToNodeId == n.Id));
        foreach (var chunk in olds.Select(o => o.Signature).Distinct().Chunk(500))
        {
            result.AddRange(await live
                .Join(_db.ArchiveItems.Where(a => a.AnalysisState == 0 && chunk.Contains(a.ContentSignature!)), n => n.Id, a => a.NodeId,
                    (n, a) => new NewRow(n.Id, n.LibraryId, n.ParentId, a.ByteLength, a.ContentSignature, a.ContentVersion, n.CreatedAt))
                .AsNoTracking().ToListAsync(ct));
        }
        foreach (var chunk in olds.Select(o => o.ByteLength).Distinct().Chunk(500))
        {
            result.AddRange(await live
                .Join(_db.ArchiveItems.Where(a => a.AnalysisState == 1 && chunk.Contains(a.ByteLength)), n => n.Id, a => a.NodeId,
                    (n, a) => new NewRow(n.Id, n.LibraryId, n.ParentId, a.ByteLength, null, a.ContentVersion, n.CreatedAt))
                .AsNoTracking().ToListAsync(ct));
        }
        return result.DistinctBy(r => r.Id).ToList();
    }

    private async Task<List<ManifestPage>> PagesAsync(long itemId, long contentVersion, CancellationToken ct) =>
        await _db.PageEntries.AsNoTracking()
            .Where(p => p.ItemId == itemId && p.ContentVersion == contentVersion)
            .OrderBy(p => p.Ordinal)
            .Select(p => new ManifestPage(p.Ordinal, p.EntryKey, p.SourceEntryLocator, p.ByteSize))
            .ToListAsync(ct);

    /// <summary>
    /// Copies the old archive's state onto the new copy in one transaction (per-table rules: the lane note's table). Returns
    /// how many conflicts were recorded.
    /// </summary>
    private async Task<int> TransferAsync(OldRow old, NewRow neu, List<ManifestPage> oldPages, List<ManifestPage> newPages,
        DateTimeOffset now, CancellationToken ct)
    {
        var map = ManifestAgreement.MapByEntry(oldPages, newPages);
        var firstPage = newPages[0];
        var conflicts = 0;
        long? droppedRecord;

        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            var moveId = await MoveConflictRecorder.EnsureMoveAsync(_db, old.Id, neu.Id, now, ct);

            // Positions map by entry; a row made against an older version of the old file keeps its state but starts at page 1.
            ManifestPage Position(long contentVersion, string entryKey) =>
                contentVersion == old.ContentVersion && map.TryGetValue(entryKey, out var p) ? p : firstPage;

            // Reading progress (unique per user and item).
            var newProgress = await _db.ReadingProgress.Where(p => p.ItemId == neu.Id).ToDictionaryAsync(p => p.UserId, ct);
            foreach (var op in await _db.ReadingProgress.Where(p => p.ItemId == old.Id).ToListAsync(ct))
            {
                var at = Position(op.ContentVersion, op.EntryKey);
                if (!newProgress.TryGetValue(op.UserId, out var np))
                {
                    op.ItemId = neu.Id;
                    op.ContentVersion = neu.ContentVersion;
                    op.Ordinal = at.Ordinal;
                    op.EntryKey = at.EntryKey;
                    op.Revision++;
                }
                else if (MoveStateRules.IsEmptyProgress(np.State, np.Ordinal))
                {
                    MoveStateRules.CopyProgress(op, np, at);
                }
                else if (!MoveStateRules.SameProgress(op.State, at.Ordinal, np.State, np.Ordinal))
                {
                    await MoveConflictRecorder.AddAsync(_db, moveId, op.UserId, MoveConflictKind.Progress, now, ct);
                    conflicts++;
                }
            }

            // Read marks: presence only - the old mark moves unless the user marked the new copy too.
            var markedNew = _db.ReadMarks.Where(m => m.ItemId == neu.Id).Select(m => m.UserId);
            await _db.ReadMarks.Where(m => m.ItemId == old.Id && !markedNew.Contains(m.UserId))
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.ItemId, neu.Id), ct);

            // Bookmarks: union by entry (only those whose position is known).
            var newBookmarks = (await _db.Bookmarks.Where(b => b.ItemId == neu.Id).Select(b => new { b.UserId, b.EntryKey }).ToListAsync(ct))
                .Select(b => (b.UserId, b.EntryKey)).ToHashSet();
            foreach (var ob in await _db.Bookmarks.Where(b => b.ItemId == old.Id).ToListAsync(ct))
            {
                if (ob.ContentVersion != old.ContentVersion || !map.TryGetValue(ob.EntryKey, out var at) || !newBookmarks.Add((ob.UserId, at.EntryKey)))
                    continue;
                ob.ItemId = neu.Id;
                ob.ContentVersion = neu.ContentVersion;
                ob.EntryKey = at.EntryKey;
                ob.Ordinal = at.Ordinal;
            }

            // Reader overrides (unique per user and item).
            var newOverrides = await _db.ItemReaderOverrides.Where(o => o.ItemId == neu.Id).ToDictionaryAsync(o => o.UserId, ct);
            foreach (var oo in await _db.ItemReaderOverrides.Where(o => o.ItemId == old.Id).ToListAsync(ct))
            {
                if (!newOverrides.TryGetValue(oo.UserId, out var no))
                    oo.ItemId = neu.Id;
                else if (!MoveStateRules.SameOverrides(oo, no))
                {
                    await MoveConflictRecorder.AddAsync(_db, moveId, oo.UserId, MoveConflictKind.ReaderSettings, now, ct);
                    conflicts++;
                }
            }

            // Favorites: presence only.
            var starredNew = _db.Favorites.Where(f => f.CatalogNodeId == neu.Id).Select(f => f.UserId);
            await _db.Favorites.Where(f => f.CatalogNodeId == old.Id && !starredNew.Contains(f.UserId))
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.CatalogNodeId, neu.Id), ct);

            // Admin rows of the archive itself: its series link (an archive that is its own work), declared facts, cover choice,
            // spread pairing. The new copy's own row wins; the link follows the link rules.
            var link = await _carryOver.ApplyLinkRulesAsync(old.Id, neu.Id, neu.LibraryId, recordConflicts: true, now, ct);
            droppedRecord = link.DroppedRecordId;
            if (link.Conflict)
                conflicts++;
            if (!await _db.DeclaredFacts.AnyAsync(f => f.NodeId == neu.Id, ct))
                await _db.DeclaredFacts.Where(f => f.NodeId == old.Id).ExecuteUpdateAsync(u => u
                    .SetProperty(f => f.NodeId, neu.Id).SetProperty(f => f.LibraryId, neu.LibraryId).SetProperty(f => f.UpdatedAt, now), ct);
            if (!await _db.NodeCoverChoices.AnyAsync(c => c.NodeId == neu.Id, ct))
                await _db.NodeCoverChoices.Where(c => c.NodeId == old.Id).ExecuteUpdateAsync(u => u
                    .SetProperty(c => c.NodeId, neu.Id).SetProperty(c => c.Version, c => c.Version + 1), ct);
            if (ManifestAgreement.SameOrder(oldPages, newPages) && !await _db.ArchiveSpreadLayouts.AnyAsync(l => l.NodeId == neu.Id, ct))
                await _db.ArchiveSpreadLayouts.Where(l => l.NodeId == old.Id && l.ContentVersion == old.ContentVersion).ExecuteUpdateAsync(u => u
                    .SetProperty(l => l.NodeId, neu.Id).SetProperty(l => l.ContentVersion, neu.ContentVersion), ct);
            // A folder cover pinned to the old file shows the new copy.
            await _db.NodeCoverChoices.Where(c => c.ArchiveNodeId == old.Id).ExecuteUpdateAsync(u => u
                .SetProperty(c => c.ArchiveNodeId, neu.Id).SetProperty(c => c.Version, c => c.Version + 1), ct);

            // A moved copy is not new content: it keeps its original Added date (and leaves New chapters).
            if (old.CreatedAt < neu.CreatedAt)
                await _db.CatalogNodes.Where(n => n.Id == neu.Id).ExecuteUpdateAsync(u => u.SetProperty(n => n.CreatedAt, old.CreatedAt), ct);

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        _db.ChangeTracker.Clear();
        if (droppedRecord is { } recordId)
            await _carryOver.RemoveOrphanRecordsAsync([recordId], ct);
        return conflicts;
    }

    /// <summary>Recomputes the recency of the new copies' folders and their ancestors (their Added dates moved back).</summary>
    private async Task RecomputeRecencyAsync(HashSet<long> folders, CancellationToken ct)
    {
        if (folders.Count == 0)
            return;
        var affected = new HashSet<long>();
        foreach (var start in folders)
        {
            long? current = start;
            for (var depth = 0; current is { } id && depth < 64 && affected.Add(id); depth++)
                current = await _db.CatalogNodes.Where(n => n.Id == id).Select(n => n.ParentId).FirstOrDefaultAsync(ct);
        }
        await LatestDescendantAddedAtMaintenance.RecomputeFoldersAsync(_db, affected, ct);
    }
}

/// <summary>Per-user state rules of a move recognised after the fact (1.31.0). Pure.</summary>
public static class MoveStateRules
{
    /// <summary>A progress row that says nothing yet: unread, at the first page.</summary>
    public static bool IsEmptyProgress(int state, int ordinal) => state == 0 && ordinal == 0;

    /// <summary>Equal progress: same state, and the same page unless both are completed.</summary>
    public static bool SameProgress(int oldState, int oldOrdinal, int newState, int newOrdinal) =>
        oldState == newState && (oldState == 2 || oldOrdinal == newOrdinal);

    public static bool SameOverrides(ItemReaderOverridesEntity a, ItemReaderOverridesEntity b) =>
        a.ReaderMode == b.ReaderMode && a.Direction == b.Direction && a.FitMode == b.FitMode
        && a.SpreadOffset == b.SpreadOffset && a.CoverOffset == b.CoverOffset && a.Background == b.Background;

    /// <summary>Puts the old progress (at the mapped page) on the new copy's row; a client holding the old revision reloads.</summary>
    public static void CopyProgress(ReadingProgressEntity from, ReadingProgressEntity to, ManifestPage at)
    {
        to.State = from.State;
        to.Ordinal = at.Ordinal;
        to.EntryKey = at.EntryKey;
        to.NormalizedAnchor = from.NormalizedAnchor;
        to.CompletedAt = from.CompletedAt;
        to.HiddenFromContinue = from.HiddenFromContinue;
        to.UpdatedAt = from.UpdatedAt > to.UpdatedAt ? from.UpdatedAt : to.UpdatedAt;
        to.Revision = Math.Max(from.Revision, to.Revision) + 1;
    }
}
