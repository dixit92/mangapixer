namespace com.lifepixer.mangapixer.Server.Scanning;

using System.Diagnostics;
using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Features.Library.Moves;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Coordinates library scans: bounded observation staging, reconciliation,
/// per-library maintenance, catalog revision bumping, and suspicious-loss detection.
///
/// Scan flow:
/// 1. Acquire persisted lease
/// 2. Enter maintenance for the affected library
/// 3. Enumerate recursively (metadata comes from the directory enumeration —
///    no per-file stat round trip)
/// 4. Reconcile observations against the catalog in bounded write batches
///    (1.5.0: one commit per <see cref="ReconcileBatchSize"/> new nodes instead of
///    two commits per node)
/// 5. Recognise moved/renamed archives by content signature and re-point the
///    existing node instead of tombstone + create (1.5.0), preserving analysis,
///    thumbnail and per-user reading state - since 1.31.0 also an archive tombstoned
///    in ANY library inside the move window (a move between libraries)
/// 6. Tombstone missing items (only after complete enumeration)
/// 7. Queue analysis for changed items
/// 8. Bump catalog revision, release maintenance
/// </summary>
public sealed class LibraryScanCoordinator
{
    /// <summary>
    /// New nodes committed per <c>SaveChanges</c> during reconciliation. Bounds
    /// transaction size (and the reader-visible window of a half-inserted
    /// subtree) while amortising the per-commit fsync — with
    /// <c>synchronous=FULL</c> a per-node commit cost ~17 ms in profiling.
    /// </summary>
    public const int ReconcileBatchSize = 500;

    private readonly MangaPixerDbContext _db;
    private readonly IReadOnlyLibraryFileSystem _fs;
    private readonly LibraryScanPolicy _policy;
    private readonly long _libraryId;
    private readonly long _scanRevision;
    private readonly string _leaseOwner;
    private readonly ILogger<LibraryScanCoordinator>? _logger;

    /// <summary>
    /// Parent folder ids whose descendant-archive set changed this scan (an archive was
    /// added, moved, resurrected from a tombstone, or tombstoned). Their
    /// <see cref="CatalogNodeEntity.LatestDescendantAddedAt"/> — and every ancestor's — is
    /// recomputed once, set-based, after reconciliation (1.12.0). Kept as the affected
    /// PARENT ids; the ancestor closure is walked at maintenance time.
    /// </summary>
    private readonly HashSet<long> _recencyParentSeeds = [];

    /// <summary>
    /// Metadata carry-over ledger (stage 2): (moved archive, its OLD parent folder)
    /// for every archive recognised as moved this scan, appended in the existing move
    /// branch only - no query, no file read, nothing when nothing moved. Capped at
    /// <see cref="MoveLedgerCap"/>; above it the ledger is dropped (carry-over skipped).
    /// </summary>
    private readonly List<ScanMove> _moveLedger = [];
    private bool _moveLedgerTruncated;

    /// <summary>
    /// Cross-library moves (1.31.0): libraries an archive was re-pointed away from this scan. Their recency is recomputed and
    /// their catalog revision bumped along with this library's.
    /// </summary>
    private readonly HashSet<long> _sourceLibraryIds = [];
    private readonly List<long> _foreignMovedNodeIds = [];

    public const int MoveLedgerCap = 100_000;

    public LibraryScanCoordinator(
        MangaPixerDbContext db,
        IReadOnlyLibraryFileSystem fs,
        LibraryScanPolicy policy,
        long libraryId,
        long scanRevision,
        string leaseOwner,
        ILogger<LibraryScanCoordinator>? logger = null)
    {
        _db = db;
        _fs = fs;
        _policy = policy;
        _libraryId = libraryId;
        _scanRevision = scanRevision;
        _leaseOwner = leaseOwner;
        _logger = logger;
    }

    /// <summary>
    /// Runs a complete scan: observation, reconciliation, and tombstoning.
    /// Returns the scan result with counts. The cancellation token is checked
    /// at every entry and during reconciliation (audit defect D34).
    /// </summary>
    public async Task<ScanResult> ScanAsync(CancellationToken ct = default)
    {
        _logger?.LogInformation(LogEvents.Scanning.ScanStarted, "Starting scan for library {LibraryId} (revision {Revision})", _libraryId, _scanRevision);

        if (!_fs.RootExists())
        {
            _logger?.LogWarning(LogEvents.Scanning.ScanRootUnavailable, "Library {LibraryId} root is not accessible", _libraryId);
            return new ScanResult
            {
                Success = false,
                Error = "root_unavailable",
                Message = "Library root is not accessible.",
            };
        }

        var total = Stopwatch.StartNew();

        // Phase 1: Bounded observation — enumerate the filesystem.
        // ParentPathKey is the relative path of the parent directory ("" for root),
        // which lets ReconcileAsync establish parent-child relationships after
        // nodes are created (audit defect D1).
        _logger?.LogDebug(LogEvents.Scanning.ScanPhaseObservation, "Scan {LibraryId} phase 1: observation", _libraryId);
        var observations = new List<ScanObservationEntity>();
        Observe("", "", observations, ct);
        var observeMs = total.ElapsedMilliseconds;
        _logger?.LogDebug(LogEvents.Scanning.ScanObservedCount, "Scan {LibraryId} observed {Count} entries", _libraryId, observations.Count);

        // Phase 2: Reconcile observations against existing catalog nodes
        _logger?.LogDebug(LogEvents.Scanning.ScanPhaseReconciliation, "Scan {LibraryId} phase 2: reconciliation", _libraryId);
        var reconcileStart = total.ElapsedMilliseconds;
        var reconciliation = await ReconcileAsync(observations, ct);
        var reconcileMs = total.ElapsedMilliseconds - reconcileStart;
        _logger?.LogDebug(LogEvents.Scanning.ScanReconciliationComplete, "Scan {LibraryId} reconciliation complete: {Added} added, {Updated} updated, {Moved} moved",
            _libraryId, reconciliation.NodesAdded, reconciliation.NodesUpdated, reconciliation.NodesMoved);

        // Phase 3: Tombstone missing nodes (only after complete observation)
        var tombstoneStart = total.ElapsedMilliseconds;
        if (reconciliation.Success)
        {
            _logger?.LogDebug(LogEvents.Scanning.ScanPhaseTombstoning, "Scan {LibraryId} phase 3: tombstoning", _libraryId);
            reconciliation.NodesTombstoned = await TombstoneMissingNodesAsync(ct);

            // Phase 3b: recency-primitive maintenance (1.12.0). Recompute
            // LatestDescendantAddedAt for every folder whose descendant-archive set changed
            // this scan, plus their ancestors — set-based, bounded to the affected paths.
            await MaintainLatestDescendantAddedAtAsync(ct);
        }
        var tombstoneMs = total.ElapsedMilliseconds - tombstoneStart;

        // Phase 4: Bump catalog revision
        var library = await _db.Libraries.FirstAsync(l => l.Id == _libraryId, ct);
        library.CatalogRevision++;
        library.LastScanCompleted = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        // An archive moved in from another library changed that library's catalog too (1.31.0).
        if (_sourceLibraryIds.Count > 0)
        {
            var sources = _sourceLibraryIds.ToList();
            await _db.Libraries.Where(l => sources.Contains(l.Id))
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.CatalogRevision, l => l.CatalogRevision + 1), ct);
        }

        _logger?.LogDebug(LogEvents.Scanning.ScanPhaseTimings, "Scan {LibraryId} timings: observe {ObserveMs} ms, reconcile {ReconcileMs} ms, tombstone {TombstoneMs} ms, total {TotalMs} ms",
            _libraryId, observeMs, reconcileMs, tombstoneMs, total.ElapsedMilliseconds);
        _logger?.LogInformation(LogEvents.Scanning.ScanCompleted, "Scan completed for library {LibraryId}: {Observed} observed, {Added} added, {Updated} updated, {Moved} moved, {Tombstoned} tombstoned",
            _libraryId, observations.Count, reconciliation.NodesAdded, reconciliation.NodesUpdated, reconciliation.NodesMoved, reconciliation.NodesTombstoned);

        return new ScanResult
        {
            Success = true,
            NodesObserved = observations.Count,
            NodesAdded = reconciliation.NodesAdded,
            NodesUpdated = reconciliation.NodesUpdated,
            NodesMoved = reconciliation.NodesMoved,
            NodesMovedFromOtherLibraries = _foreignMovedNodeIds.Count,
            NodesTombstoned = reconciliation.NodesTombstoned,
            Moves = _moveLedgerTruncated ? [] : _moveLedger,
            MoveLedgerTruncated = _moveLedgerTruncated,
        };
    }

    /// <summary>
    /// Recursive enumeration. Byte length and last-write time are taken from the
    /// enumeration entry itself: the directory listing already carries them
    /// (Windows) or the entry has already been stat'ed for its attributes (Unix),
    /// so the former per-file <c>GetSourceStamp</c> call — an extra exists-check
    /// plus stat per archive, i.e. two or three more round trips on an SMB/NFS
    /// share — is avoided. The ticks are identical to what <c>GetSourceStamp</c>
    /// and the worker report (<c>LastWriteTimeUtc.Ticks</c>).
    /// </summary>
    private void Observe(
        string relativePath,
        string parentPathKey,
        List<ScanObservationEntity> observations,
        CancellationToken ct)
    {
        var entries = _fs.EnumerateEntries(relativePath);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Kind == EntryKind.Directory)
            {
                if (!_policy.ShouldTraverseDirectory(entry))
                    continue;

                observations.Add(new ScanObservationEntity
                {
                    ScanRunId = 0, // Set by caller
                    LibraryId = _libraryId,
                    RelativePath = entry.RelativePath,
                    PathKey = entry.RelativePath,
                    Kind = 0, // folder
                    DisplayName = entry.Name,
                    ParentPathKey = parentPathKey,
                    ObservationStatus = 0,
                });

                // Recurse into the directory — the directory's own PathKey
                // becomes the ParentPathKey for its children (D1 fix).
                Observe(entry.RelativePath, entry.RelativePath, observations, ct);
            }
            else if (entry.Kind == EntryKind.File)
            {
                if (!_policy.IsArchiveCandidate(entry))
                    continue;

                observations.Add(new ScanObservationEntity
                {
                    ScanRunId = 0,
                    LibraryId = _libraryId,
                    RelativePath = entry.RelativePath,
                    PathKey = entry.RelativePath,
                    Kind = 1, // archive
                    DisplayName = entry.Name,
                    ParentPathKey = parentPathKey,
                    ByteLength = entry.ByteLength,
                    ModificationTicks = entry.LastWriteTimeUtc.UtcTicks,
                    ObservationStatus = 0,
                });
            }
        }
    }

    private async Task<ReconciliationResult> ReconcileAsync(
        List<ScanObservationEntity> observations,
        CancellationToken ct)
    {
        var result = new ReconciliationResult();

        // Sort observations by path depth (parents first) so that parent
        // nodes are visited before children and the pathKey→node map can be
        // filled incrementally (audit defect D1).
        var sorted = observations
            .OrderBy(o => o.PathKey.Count(c => c == '/' || c == '\\'))
            .ThenBy(o => o.PathKey, StringComparer.Ordinal)
            .ToList();

        // Load existing nodes and archive items for this library in two queries.
        // Previously the archive item was fetched per node — and, in a fresh
        // scope, never at all: the `ArchiveItem` navigation was not loaded, so
        // in-place content changes were silently missed. EF fix-up links the two
        // sets once both are tracked.
        var existingNodes = await _db.CatalogNodes
            .Where(n => n.LibraryId == _libraryId)
            .ToDictionaryAsync(n => n.PathKey, StringComparer.Ordinal, ct);
        var archiveItems = await _db.ArchiveItems
            .Where(a => a.Node!.LibraryId == _libraryId)
            .ToDictionaryAsync(a => a.NodeId, ct);

        var observedPathKeys = new HashSet<string>(observations.Select(o => o.PathKey), StringComparer.Ordinal);

        // 1.5.0: archives that vanished from their old path but reappear elsewhere
        // with the same size + content signature are moves, not remove+add.
        // 1.31.0: the pool also holds archives tombstoned in any library inside the move window.
        var pool = await LoadMovePoolAsync(existingNodes, archiveItems, observedPathKeys, ct);
        var moves = await DetectMovesAsync(sorted, existingNodes, archiveItems, pool, ct);

        // pathKey→node map (existing or newly added, possibly not yet saved).
        // Parentage is expressed through the `Parent` navigation so EF resolves
        // ParentId itself, whether the parent was committed in an earlier batch
        // or is inserted in the same one — this is what allows batching.
        var pathToNode = new Dictionary<string, CatalogNodeEntity>(existingNodes, StringComparer.Ordinal);

        var pendingInBatch = 0;
        var now = DateTimeOffset.UtcNow;

        // Recency-primitive maintenance (1.12.0): archives whose presence/location changed
        // this scan. Their parent ids are read AFTER the final save (a freshly-added folder's
        // id is assigned then); moves also contribute their OLD parent id, captured before it
        // is overwritten. See MaintainLatestDescendantAddedAtAsync.
        var recencyAffectedArchives = new List<CatalogNodeEntity>();
        var movedOldParentIds = new List<long>();

        foreach (var obs in sorted)
        {
            ct.ThrowIfCancellationRequested();

            // Resolve parent from the path map. If the parent path key is not
            // in the map, the parent was filtered out (e.g., not traversed);
            // leave it null so the node appears at the root of its accessible
            // subtree.
            CatalogNodeEntity? parent = null;
            if (!string.IsNullOrEmpty(obs.ParentPathKey))
                pathToNode.TryGetValue(obs.ParentPathKey, out parent);

            if (existingNodes.TryGetValue(obs.PathKey, out var existing))
            {
                var needsUpdate = false;

                if (existing.Kind != obs.Kind)
                {
                    existing.Kind = obs.Kind;
                    needsUpdate = true;
                }

                if (existing.DisplayName != obs.DisplayName)
                {
                    existing.DisplayName = obs.DisplayName;
                    needsUpdate = true;
                }

                if (existing.Availability == 5) // was tombstoned
                {
                    existing.Availability = 0; // available again
                    existing.TombstonedAt = null;
                    needsUpdate = true;
                    // A resurrected archive re-enters its ancestors' descendant set.
                    if (existing.Kind == 1)
                        recencyAffectedArchives.Add(existing);
                }

                // Repair parent if it differs (fixes libraries scanned before
                // the D1 hierarchy fix). An unsaved parent (Id == 0) is always
                // a change.
                if (!HasParent(existing, parent))
                {
                    SetParent(existing, parent);
                    needsUpdate = true;
                }

                existing.LastSeenScanRevision = _scanRevision;

                if (needsUpdate)
                {
                    existing.UpdatedAt = now;
                    result.NodesUpdated++;
                }

                // Archives: detect in-place content change by source stamp.
                if (existing.Kind == 1 && archiveItems.TryGetValue(existing.Id, out var archiveItem))
                {
                    if (archiveItem.ByteLength != obs.ByteLength || archiveItem.ModificationTicks != obs.ModificationTicks)
                    {
                        archiveItem.ContentVersion++;
                        archiveItem.ByteLength = obs.ByteLength;
                        archiveItem.ModificationTicks = obs.ModificationTicks;
                        // The stored signature described the old bytes; the next
                        // analysis of the new content recomputes it. Until then
                        // this row must not match as a move.
                        archiveItem.ContentSignature = null;
                        // Queue re-analysis: the page manifest belongs to the old
                        // content version. (This branch never ran in production
                        // before 1.5.0 — see the loading note above — so the
                        // post-scan enqueue, which selects pending items, is the
                        // natural trigger.)
                        archiveItem.AnalysisState = 1; // pending
                        archiveItem.AnalysisError = null;
                        result.NodesUpdated++;
                    }
                }
            }
            else if (moves.TryGetValue(obs.PathKey, out var moved))
            {
                // Same content at a new path: re-point the existing node. Node id,
                // ContentVersion, page manifest, thumbnail and every per-user row
                // keyed by ItemId survive; no re-analysis is queued.
                // Capture the OLD parent before it is overwritten so its (now smaller)
                // descendant set is recomputed alongside the new location's.
                if (moved.ParentId is long oldParentId)
                {
                    movedOldParentIds.Add(oldParentId);
                    if (_moveLedger.Count < MoveLedgerCap)
                        _moveLedger.Add(new ScanMove(moved.Id, oldParentId));
                    else
                        _moveLedgerTruncated = true;
                }
                recencyAffectedArchives.Add(moved);
                moved.RelativePath = obs.RelativePath;
                moved.PathKey = obs.PathKey;
                moved.DisplayName = obs.DisplayName;
                moved.SortKey = BuildSortKey(obs.Kind, obs.DisplayName);
                SetParent(moved, parent);
                moved.Availability = 0;
                moved.TombstonedAt = null;
                moved.LastSeenScanRevision = _scanRevision;
                moved.UpdatedAt = now;

                // A copy+delete style move may carry a fresh mtime. The bytes were
                // verified by signature, so record the new stamp without bumping
                // ContentVersion (a bump would invalidate progress and the thumbnail).
                var movedItem = archiveItems[moved.Id];
                movedItem.ModificationTicks = obs.ModificationTicks;
                movedItem.ByteLength = obs.ByteLength;

                pathToNode[obs.PathKey] = moved;
                result.NodesMoved++;
                _logger?.LogDebug(LogEvents.Scanning.ScanMoveDetected, "Scan {LibraryId}: node {NodeId} recognised at a new path (move)", _libraryId, moved.Id);
            }
            else
            {
                var node = new CatalogNodeEntity
                {
                    PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
                    LibraryId = _libraryId,
                    Kind = obs.Kind,
                    DisplayName = obs.DisplayName,
                    RelativePath = obs.RelativePath,
                    PathKey = obs.PathKey,
                    SortKey = BuildSortKey(obs.Kind, obs.DisplayName),
                    Availability = 0,
                    LastSeenScanRevision = _scanRevision,
                    CreatedAt = now,
                };
                SetParent(node, parent);

                if (obs.Kind == 1)
                {
                    node.ArchiveItem = new ArchiveItemEntity
                    {
                        ArchiveFormat = 0, // Unknown until analysis
                        ByteLength = obs.ByteLength,
                        ModificationTicks = obs.ModificationTicks,
                        ContentVersion = 1,
                        AnalysisState = 1, // pending
                    };
                }

                _db.CatalogNodes.Add(node);
                pathToNode[obs.PathKey] = node;
                if (obs.Kind == 1)
                    recencyAffectedArchives.Add(node);
                result.NodesAdded++;

                if (++pendingInBatch >= ReconcileBatchSize)
                {
                    await _db.SaveChangesAsync(ct);
                    pendingInBatch = 0;
                }
            }
        }

        await _db.SaveChangesAsync(ct);

        // Cross-library moves (1.31.0): the denormalised library of the moved archives' own rows follows them.
        await UpdateDenormalisedLibraryAsync(ct);

        // Now that every add is persisted (parent ids assigned), record the affected
        // parent folders for recency-primitive maintenance.
        foreach (var archive in recencyAffectedArchives)
            if (archive.ParentId is long pid)
                _recencyParentSeeds.Add(pid);
        foreach (var oldParentId in movedOldParentIds)
            _recencyParentSeeds.Add(oldParentId);

        if (result.NodesMoved > 0)
            _logger?.LogInformation(LogEvents.Scanning.ScanMovesApplied, "Scan {LibraryId}: {Count} archives recognised as moved (analysis and reading state preserved)", _libraryId, result.NodesMoved);
        if (_foreignMovedNodeIds.Count > 0)
            _logger?.LogInformation(LogEvents.Scanning.ScanCrossLibraryMoves, "Scan {LibraryId}: {Count} archives moved in from {Libraries} other libraries",
                _libraryId, _foreignMovedNodeIds.Count, _sourceLibraryIds.Count);

        result.Success = true;
        return result;
    }

    private static bool HasParent(CatalogNodeEntity node, CatalogNodeEntity? parent)
    {
        if (parent is null) return node.ParentId is null && node.Parent is null;
        if (parent.Id == 0) return ReferenceEquals(node.Parent, parent);
        return node.ParentId == parent.Id;
    }

    /// <summary>
    /// Sets both the navigation and, when the parent already has a key, the FK,
    /// so the change is unambiguous to EF's change tracker whether the parent was
    /// loaded, saved in an earlier batch, or is pending in the current one.
    /// </summary>
    private static void SetParent(CatalogNodeEntity node, CatalogNodeEntity? parent)
    {
        node.Parent = parent;
        if (parent is null)
            node.ParentId = null;
        else if (parent.Id != 0)
            node.ParentId = parent.Id;
    }

    /// <summary>One archive the scan may recognise at a new path (1.31.0 pool).</summary>
    private sealed record MoveCandidate(
        long NodeId, long LibraryId, long ByteLength, string Signature, bool IsTombstone,
        long LastSeenScanRevision, DateTimeOffset? TombstonedAt, CatalogNodeEntity? Tracked);

    /// <summary>
    /// The move pool: this library's archives missing from their path (1.5.0), plus archives tombstoned in ANY library -
    /// this one included - inside the move window (1.31.0), with a usable signature, not already handled by a move
    /// recognised after the fact, and (for another library) not while that library is being scanned. One projection query
    /// for the other libraries; this library's rows are already tracked.
    /// </summary>
    private async Task<List<MoveCandidate>> LoadMovePoolAsync(
        Dictionary<string, CatalogNodeEntity> existingNodes,
        Dictionary<long, ArchiveItemEntity> archiveItems,
        HashSet<string> observedPathKeys,
        CancellationToken ct)
    {
        var pool = new List<MoveCandidate>();
        var now = DateTimeOffset.UtcNow;
        var windowStart = await MoveEvidence.WindowStartAsync(_db, now, ct);
        var handled = (await _db.NodeMoves.AsNoTracking()
                .Where(m => m.FromNode!.LibraryId == _libraryId)
                .Select(m => m.FromNodeId)
                .ToListAsync(ct))
            .ToHashSet();

        foreach (var node in existingNodes.Values)
        {
            if (node.Kind != 1 || observedPathKeys.Contains(node.PathKey))
                continue;
            if (!archiveItems.TryGetValue(node.Id, out var item) || !MoveEvidence.IsUsable(item.ContentSignature, item.ByteLength))
                continue;
            var tombstone = node.Availability == MoveEvidence.Tombstoned;
            if (tombstone && (node.TombstonedAt is not { } at || at < windowStart || handled.Contains(node.Id)))
                continue;
            pool.Add(new MoveCandidate(node.Id, _libraryId, item.ByteLength, item.ContentSignature!, tombstone,
                node.LastSeenScanRevision, node.TombstonedAt, node));
        }

        var tombstoned = MoveEvidence.Tombstoned;
        var running = MoveEvidence.RunningScanLibraryIds(_db, now);
        var foreign = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.Kind == 1 && n.Availability == tombstoned && n.LibraryId != _libraryId
                && n.TombstonedAt != null && n.TombstonedAt >= windowStart
                && !_db.NodeMoves.Any(m => m.FromNodeId == n.Id)
                && !running.Contains(n.LibraryId))
            .Join(_db.ArchiveItems.Where(a => a.ContentSignature != null), n => n.Id, a => a.NodeId,
                (n, a) => new { n.Id, n.LibraryId, a.ByteLength, a.ContentSignature, n.LastSeenScanRevision, n.TombstonedAt })
            .ToListAsync(ct);
        foreach (var f in foreign)
            if (MoveEvidence.IsUsable(f.ContentSignature, f.ByteLength))
                pool.Add(new MoveCandidate(f.Id, f.LibraryId, f.ByteLength, f.ContentSignature!, true,
                    f.LastSeenScanRevision, f.TombstonedAt, null));
        return pool;
    }

    /// <summary>
    /// Pairs pool archives with new observations that have the same byte length and content signature. Returns
    /// new-PathKey -> node.
    ///
    /// Guards (a wrong match corrupts the catalog and destroys reading state):
    /// - the old row must carry a signature (legacy / never-analysed rows never match);
    /// - byte length must agree both via the observation and the stored row;
    /// - the new file's signature is computed from the bytes on disk and its
    ///   stamp re-checked afterwards, so a file still being written never matches;
    /// - the pairing must be unambiguous: exactly one pool row (missing here or tombstoned
    ///   anywhere inside the window) and exactly one new file share the signature;
    /// - for a tombstone, no live archive elsewhere that appeared after the tombstone was last
    ///   seen carries the signature (a second copy makes it ambiguous);
    /// - an archive of another library is claimed with one conditional update before it is
    ///   re-pointed; a lost claim leaves the observation a new node.
    /// Duplicates fall back to remove + add.
    /// Cost: one 128 KiB read per new archive whose size equals a pool row's;
    /// nothing is read when the pool is empty.
    /// </summary>
    private async Task<Dictionary<string, CatalogNodeEntity>> DetectMovesAsync(
        List<ScanObservationEntity> sorted,
        Dictionary<string, CatalogNodeEntity> existingNodes,
        Dictionary<long, ArchiveItemEntity> archiveItems,
        List<MoveCandidate> pool,
        CancellationToken ct)
    {
        var moves = new Dictionary<string, CatalogNodeEntity>(StringComparer.Ordinal);
        if (pool.Count == 0)
            return moves;
        var poolBySize = pool.GroupBy(p => p.ByteLength).ToDictionary(g => g.Key, g => g.ToList());

        var candidatesBySignature = new Dictionary<string, List<ScanObservationEntity>>(StringComparer.Ordinal);
        foreach (var obs in sorted)
        {
            if (obs.Kind != 1 || existingNodes.ContainsKey(obs.PathKey) || !poolBySize.ContainsKey(obs.ByteLength))
                continue;

            var signature = ComputeObservedSignature(obs);
            if (signature is null)
                continue;

            if (!candidatesBySignature.TryGetValue(signature, out var list))
                candidatesBySignature[signature] = list = [];
            list.Add(obs);
        }

        var pairs = new List<(ScanObservationEntity Obs, MoveCandidate Old)>();
        foreach (var (signature, candidates) in candidatesBySignature)
        {
            var size = candidates[0].ByteLength;
            var matchingRows = poolBySize[size]
                .Where(m => string.Equals(m.Signature, signature, StringComparison.Ordinal))
                .ToList();

            if (matchingRows.Count == 0)
                continue;

            if (matchingRows.Count != 1 || candidates.Count != 1)
            {
                _logger?.LogDebug(LogEvents.Scanning.ScanMoveAmbiguous, "Scan {LibraryId}: {Missing} missing and {New} new archives share one signature; not treated as a move", _libraryId, matchingRows.Count, candidates.Count);
                continue;
            }
            pairs.Add((candidates[0], matchingRows[0]));
        }

        var tombstonePairs = pairs.Where(p => p.Old.IsTombstone).ToList();
        var copiedElsewhere = await CopiedElsewhereAsync(tombstonePairs.Select(p => p.Old).ToList(), ct);
        foreach (var (obs, old) in pairs)
        {
            if (copiedElsewhere.Contains(old.NodeId))
            {
                _logger?.LogDebug(LogEvents.Scanning.ScanMoveAmbiguous, "Scan {LibraryId}: node {NodeId} has another live copy; not treated as a move", _libraryId, old.NodeId);
                continue;
            }
            if (old.Tracked is { } tracked)
            {
                moves[obs.PathKey] = tracked;
                continue;
            }

            // Another library's tombstone: claim it (still a hidden tombstone, now at its new path here). A scan that fails
            // after this point leaves it where the next scan of this library resurrects it by path.
            var claimed = await _db.CatalogNodes
                .Where(n => n.Id == old.NodeId && n.Availability == MoveEvidence.Tombstoned && n.LibraryId == old.LibraryId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(n => n.LibraryId, _libraryId)
                    .SetProperty(n => n.PathKey, obs.PathKey)
                    .SetProperty(n => n.RelativePath, obs.RelativePath), ct);
            if (claimed != 1)
            {
                _logger?.LogDebug(LogEvents.Scanning.ScanMoveClaimLost, "Scan {LibraryId}: node {NodeId} was taken before it could be moved here", _libraryId, old.NodeId);
                continue;
            }
            var node = await _db.CatalogNodes.FirstAsync(n => n.Id == old.NodeId, ct);
            var item = await _db.ArchiveItems.FirstAsync(a => a.NodeId == old.NodeId, ct);
            archiveItems[node.Id] = item;
            moves[obs.PathKey] = node;
            _sourceLibraryIds.Add(old.LibraryId);
            _foreignMovedNodeIds.Add(node.Id);
        }

        return moves;
    }

    /// <summary>
    /// Tombstones of the pool for which a LIVE archive with the same signature already appeared after the tombstone was last
    /// seen - in this or another library. Such a second copy makes the move ambiguous (copies stay separate items).
    /// </summary>
    private async Task<HashSet<long>> CopiedElsewhereAsync(List<MoveCandidate> tombstones, CancellationToken ct)
    {
        var result = new HashSet<long>();
        if (tombstones.Count == 0)
            return result;
        var lastSeen = await MoveEvidence.LastSeenAtAsync(_db,
            tombstones.Select(t => new MoveEvidence.Sighting(t.NodeId, t.LibraryId, t.LastSeenScanRevision, t.TombstonedAt)).ToList(), ct);
        var tombstoned = MoveEvidence.Tombstoned;
        foreach (var chunk in tombstones.Chunk(500))
        {
            var signatures = chunk.Select(t => t.Signature).Distinct().ToList();
            var live = await _db.ArchiveItems.AsNoTracking()
                .Where(a => signatures.Contains(a.ContentSignature!))
                .Join(_db.CatalogNodes.Where(n => n.Availability != tombstoned), a => a.NodeId, n => n.Id,
                    (a, n) => new { a.ContentSignature, n.CreatedAt })
                .ToListAsync(ct);
            foreach (var t in chunk)
                if (live.Any(l => l.ContentSignature == t.Signature && l.CreatedAt > lastSeen[t.NodeId]))
                    result.Add(t.NodeId);
        }
        return result;
    }

    /// <summary>
    /// The rows that carry an archive's library besides the node itself (series link, declared facts, match queue, flags)
    /// follow an archive moved in from another library. History (jobs, scan runs, audit, match runs) keeps where it happened.
    /// The search index follows by its own trigger on <c>catalog_nodes.LibraryId</c>.
    /// </summary>
    private async Task UpdateDenormalisedLibraryAsync(CancellationToken ct)
    {
        foreach (var chunk in _foreignMovedNodeIds.Chunk(500))
        {
            var ids = chunk.ToList();
            await _db.NodeSeriesLinks.Where(l => ids.Contains(l.NodeId)).ExecuteUpdateAsync(u => u.SetProperty(l => l.LibraryId, _libraryId), ct);
            await _db.DeclaredFacts.Where(f => f.NodeId != null && ids.Contains(f.NodeId.Value)).ExecuteUpdateAsync(u => u.SetProperty(f => f.LibraryId, _libraryId), ct);
            await _db.MetadataMatchQueue.Where(q => ids.Contains(q.NodeId)).ExecuteUpdateAsync(u => u.SetProperty(q => q.LibraryId, _libraryId), ct);
            await _db.MetadataFlags.Where(f => ids.Contains(f.NodeId)).ExecuteUpdateAsync(u => u.SetProperty(f => f.LibraryId, _libraryId), ct);
        }
    }

    private string? ComputeObservedSignature(ScanObservationEntity obs)
    {
        try
        {
            string signature;
            using (var stream = _fs.OpenRead(obs.RelativePath))
                signature = ContentSignature.Compute(stream);

            // Re-check the stamp: a file still being copied in could have grown
            // or been rewritten between enumeration and hashing.
            var stamp = _fs.GetSourceStamp(obs.RelativePath);
            if (stamp.ByteLength != obs.ByteLength || stamp.LastWriteTicks != obs.ModificationTicks)
                return null;

            return signature;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private async Task<int> TombstoneMissingNodesAsync(CancellationToken ct)
    {
        // Find all nodes that were not seen in this scan
        var missingNodes = await _db.CatalogNodes
            .Where(n => n.LibraryId == _libraryId && n.LastSeenScanRevision < _scanRevision && n.Availability != 5)
            .ToListAsync(ct);

        if (missingNodes.Count == 0)
        {
            _logger?.LogDebug(LogEvents.Scanning.ScanNoMissingNodes, "Scan {LibraryId}: no missing nodes to tombstone", _libraryId);
            return 0;
        }

        _logger?.LogDebug(LogEvents.Scanning.ScanMissingNodes, "Scan {LibraryId}: {Count} nodes missing since last scan", _libraryId, missingNodes.Count);

        // Suspicious-loss detection
        var totalNodes = await _db.CatalogNodes.CountAsync(n => n.LibraryId == _libraryId, ct);
        var archiveCount = missingNodes.Count(n => n.Kind == 1);

        // Default threshold: at least 100 previously present archives and more than 90% absent
        // Plus always-suspicious: nonempty-to-empty
        if (archiveCount >= 100 && (double)missingNodes.Count / totalNodes > 0.9)
        {
            _logger?.LogWarning(LogEvents.Scanning.SuspiciousLossPartial, "Suspicious loss detected: {MissingCount} of {TotalCount} nodes missing in library {LibraryId}. Requires admin review.", missingNodes.Count, totalNodes, _libraryId);
            // Do not tombstone — require admin review
            return 0;
        }

        if (totalNodes > 0 && missingNodes.Count == totalNodes)
        {
            _logger?.LogWarning(LogEvents.Scanning.SuspiciousLossAll, "Suspicious loss: all {TotalCount} nodes missing in library {LibraryId}. Requires admin review.", totalNodes, _libraryId);
            return 0;
        }

        // Tombstone the missing nodes. TombstonedAt (1.31.0) starts the retention window: a move candidate inside it, trash after it.
        var tombstonedAt = DateTimeOffset.UtcNow;
        foreach (var node in missingNodes)
        {
            node.Availability = 5; // tombstoned
            node.UpdatedAt = tombstonedAt;
            node.TombstonedAt = tombstonedAt;
            // A tombstoned node leaves its ancestors' descendant-archive set (directly, for
            // an archive; via its own tombstoned descendants, for a folder). Recompute those
            // ancestors from the parent up.
            if (node.ParentId is long pid)
                _recencyParentSeeds.Add(pid);
        }

        await _db.SaveChangesAsync(ct);
        _logger?.LogInformation(LogEvents.Scanning.ScanTombstoned, "Scan {LibraryId}: tombstoned {Count} missing nodes", _libraryId, missingNodes.Count);
        return missingNodes.Count;
    }

    /// <summary>
    /// Recomputes <see cref="CatalogNodeEntity.LatestDescendantAddedAt"/> for every folder
    /// whose descendant-archive set changed this scan and every ancestor of those folders
    /// (1.12.0). The seed set (<see cref="_recencyParentSeeds"/>) holds the affected PARENT
    /// ids; the ancestor closure is walked in memory from a single (Id, ParentId) projection
    /// of this library, then one set-based recursive-CTE UPDATE recomputes exactly the
    /// affected folders — no per-node round trip. Preserves the invariant: a folder's value
    /// is the MAX CreatedAt of its non-tombstoned descendant archives, or null.
    /// </summary>
    private async Task MaintainLatestDescendantAddedAtAsync(CancellationToken ct)
    {
        if (_recencyParentSeeds.Count == 0)
            return;

        // Parent map for this library (one projection). Walking up from each seed collects the
        // affected folder + all of its ancestors, bounded to the touched paths.
        // A move from another library (1.31.0) also seeds the old parent in that library: walk its tree too.
        var libraries = _sourceLibraryIds.Append(_libraryId).ToList();
        var parentMap = await _db.CatalogNodes
            .Where(n => libraries.Contains(n.LibraryId))
            .Select(n => new { n.Id, n.ParentId })
            .ToDictionaryAsync(x => x.Id, x => x.ParentId, ct);

        var affected = new HashSet<long>();
        foreach (var seed in _recencyParentSeeds)
        {
            var current = (long?)seed;
            // Follow the parent chain inclusive of the seed; stop at the root or a cycle.
            while (current is long id && affected.Add(id))
                current = parentMap.TryGetValue(id, out var parent) ? parent : null;
        }

        if (affected.Count > 0)
            await LatestDescendantAddedAtMaintenance.RecomputeFoldersAsync(_db, affected, ct);
    }

    /// <summary>
    /// Builds the persisted sort key for an observed node.
    ///
    /// Delegates to the shared <see cref="SortKey.ForNode"/> encoder, which is the
    /// single source of truth for the on-disk key format. Until 1.15.0 this method
    /// stored the RAW display name behind the kind prefix, so "Chapter 10" sorted
    /// before "Chapter 2" everywhere the catalog is ordered - the encoder existed but
    /// was only ever called from tests. Any change here must be matched by a
    /// backfill migration (BackfillNaturalSortKeys).
    /// </summary>
    private static string BuildSortKey(int kind, string name) =>
        SortKey.ForNode((CatalogNodeKind)kind, name);

    private sealed class ReconciliationResult
    {
        public bool Success { get; set; }
        public int NodesAdded { get; set; }
        public int NodesUpdated { get; set; }
        public int NodesMoved { get; set; }
        public int NodesTombstoned { get; set; }
    }
}

/// <summary>
/// Result of a library scan.
/// </summary>
public sealed class ScanResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public int NodesObserved { get; init; }
    public int NodesAdded { get; init; }
    public int NodesUpdated { get; init; }

    /// <summary>
    /// Archives recognised at a new path by content signature and re-pointed in
    /// place (1.5.0). Not counted in <see cref="NodesAdded"/> or tombstoned.
    /// </summary>
    public int NodesMoved { get; init; }

    /// <summary>Of <see cref="NodesMoved"/>, archives that were tombstoned in another library (1.31.0).</summary>
    public int NodesMovedFromOtherLibraries { get; init; }
    public int NodesTombstoned { get; init; }

    /// <summary>Metadata carry-over ledger: moved archives with their old parent folder (empty when none, or truncated).</summary>
    public IReadOnlyList<ScanMove> Moves { get; init; } = [];

    /// <summary>True when more than <see cref="LibraryScanCoordinator.MoveLedgerCap"/> archives moved (carry-over skipped).</summary>
    public bool MoveLedgerTruncated { get; init; }
}

/// <summary>An archive recognised as moved by a scan, with the folder it was in before.</summary>
public readonly record struct ScanMove(long ArchiveNodeId, long OldParentId);
