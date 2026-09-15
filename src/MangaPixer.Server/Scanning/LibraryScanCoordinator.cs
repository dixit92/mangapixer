namespace com.lifepixer.mangapixer.Server.Scanning;

using System.Diagnostics;
using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Media;
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
///    thumbnail and per-user reading state
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
            NodesTombstoned = reconciliation.NodesTombstoned,
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
        var moves = DetectMoves(sorted, existingNodes, archiveItems, observedPathKeys);

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
                    movedOldParentIds.Add(oldParentId);
                recencyAffectedArchives.Add(moved);
                moved.RelativePath = obs.RelativePath;
                moved.PathKey = obs.PathKey;
                moved.DisplayName = obs.DisplayName;
                moved.SortKey = BuildSortKey(obs.Kind, obs.DisplayName);
                SetParent(moved, parent);
                moved.Availability = 0;
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

        // Now that every add is persisted (parent ids assigned), record the affected
        // parent folders for recency-primitive maintenance.
        foreach (var archive in recencyAffectedArchives)
            if (archive.ParentId is long pid)
                _recencyParentSeeds.Add(pid);
        foreach (var oldParentId in movedOldParentIds)
            _recencyParentSeeds.Add(oldParentId);

        if (result.NodesMoved > 0)
            _logger?.LogInformation(LogEvents.Scanning.ScanMovesApplied, "Scan {LibraryId}: {Count} archives recognised as moved (analysis and reading state preserved)", _libraryId, result.NodesMoved);

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

    /// <summary>
    /// Pairs archives missing from their old path with new observations that have
    /// the same byte length and content signature. Returns new-PathKey → node.
    ///
    /// Guards (a wrong match corrupts the catalog and destroys reading state):
    /// - the old row must carry a signature (legacy / never-analysed rows never match);
    /// - byte length must agree both via the observation and the stored row;
    /// - the new file's signature is computed from the bytes on disk and its
    ///   stamp re-checked afterwards, so a file still being written never matches;
    /// - the pairing must be unambiguous: exactly one missing row and exactly one
    ///   new file share the signature. Duplicates fall back to remove + add.
    /// Cost: one 128 KiB read per new archive whose size equals a missing one;
    /// nothing is read when there are no missing archives.
    /// </summary>
    private Dictionary<string, CatalogNodeEntity> DetectMoves(
        List<ScanObservationEntity> sorted,
        Dictionary<string, CatalogNodeEntity> existingNodes,
        Dictionary<long, ArchiveItemEntity> archiveItems,
        HashSet<string> observedPathKeys)
    {
        var moves = new Dictionary<string, CatalogNodeEntity>(StringComparer.Ordinal);

        var missingBySize = new Dictionary<long, List<(CatalogNodeEntity Node, ArchiveItemEntity Item)>>();
        foreach (var node in existingNodes.Values)
        {
            if (node.Kind != 1 || node.Availability == 5 || observedPathKeys.Contains(node.PathKey))
                continue;
            if (!archiveItems.TryGetValue(node.Id, out var item) || string.IsNullOrEmpty(item.ContentSignature))
                continue;
            // Defensive: a stored signature must describe the stored length.
            if (ContentSignature.TryGetByteLength(item.ContentSignature) != item.ByteLength)
                continue;

            if (!missingBySize.TryGetValue(item.ByteLength, out var list))
                missingBySize[item.ByteLength] = list = [];
            list.Add((node, item));
        }

        if (missingBySize.Count == 0)
            return moves;

        var candidatesBySignature = new Dictionary<string, List<ScanObservationEntity>>(StringComparer.Ordinal);
        foreach (var obs in sorted)
        {
            if (obs.Kind != 1 || existingNodes.ContainsKey(obs.PathKey) || !missingBySize.ContainsKey(obs.ByteLength))
                continue;

            var signature = ComputeObservedSignature(obs);
            if (signature is null)
                continue;

            if (!candidatesBySignature.TryGetValue(signature, out var list))
                candidatesBySignature[signature] = list = [];
            list.Add(obs);
        }

        foreach (var (signature, candidates) in candidatesBySignature)
        {
            var size = candidates[0].ByteLength;
            var matchingRows = missingBySize[size]
                .Where(m => m.Item.ByteLength == size && string.Equals(m.Item.ContentSignature, signature, StringComparison.Ordinal))
                .ToList();

            if (matchingRows.Count == 0)
                continue;

            if (matchingRows.Count != 1 || candidates.Count != 1)
            {
                _logger?.LogDebug(LogEvents.Scanning.ScanMoveAmbiguous, "Scan {LibraryId}: {Missing} missing and {New} new archives share one signature; not treated as a move", _libraryId, matchingRows.Count, candidates.Count);
                continue;
            }

            moves[candidates[0].PathKey] = matchingRows[0].Node;
        }

        return moves;
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

        // Tombstone the missing nodes
        foreach (var node in missingNodes)
        {
            node.Availability = 5; // tombstoned
            node.UpdatedAt = DateTimeOffset.UtcNow;
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
        var parentMap = await _db.CatalogNodes
            .Where(n => n.LibraryId == _libraryId)
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

    private static string BuildSortKey(int kind, string name)
    {
        // Folders sort before archives: prefix with 0 for folders, 1 for archives
        var prefix = kind == 0 ? "0" : "1";
        return prefix + name;
    }

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
    public int NodesTombstoned { get; init; }
}
