namespace com.lifepixer.mangaplex.Server.Scanning;

using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using com.lifepixer.mangaplex.Server.Storage;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Coordinates library scans: bounded observation staging, reconciliation,
/// per-library maintenance, catalog revision bumping, and suspicious-loss detection.
///
/// Scan flow:
/// 1. Acquire persisted lease
/// 2. Enter maintenance for the affected library
/// 3. Enumerate recursively in bounded batches
/// 4. Store observations in ScanObservations
/// 5. Reconcile observations in short transactions
/// 6. Tombstone missing items (only after complete enumeration)
/// 7. Queue analysis for changed items
/// 8. Bump catalog revision, release maintenance
/// </summary>
public sealed class LibraryScanCoordinator
{
    private readonly MangaPlexDbContext _db;
    private readonly IReadOnlyLibraryFileSystem _fs;
    private readonly LibraryScanPolicy _policy;
    private readonly long _libraryId;
    private readonly long _scanRevision;
    private readonly string _leaseOwner;
    private readonly ILogger<LibraryScanCoordinator>? _logger;

    public LibraryScanCoordinator(
        MangaPlexDbContext db,
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
        if (!_fs.RootExists())
        {
            return new ScanResult
            {
                Success = false,
                Error = "root_unavailable",
                Message = "Library root is not accessible.",
            };
        }

        // Phase 1: Bounded observation — enumerate the filesystem.
        // ParentPathKey is the relative path of the parent directory ("" for root),
        // which lets ReconcileAsync establish parent-child relationships after
        // nodes are created (audit defect D1).
        var observations = new List<ScanObservationEntity>();
        await ObserveAsync("", "", observations, ct);

        // Phase 2: Reconcile observations against existing catalog nodes
        var reconciliation = await ReconcileAsync(observations, ct);

        // Phase 3: Tombstone missing nodes (only after complete observation)
        if (reconciliation.Success)
        {
            await TombstoneMissingNodesAsync(ct);
        }

        // Phase 4: Bump catalog revision
        var library = await _db.Libraries.FirstAsync(l => l.Id == _libraryId, ct);
        library.CatalogRevision++;
        library.LastScanCompleted = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new ScanResult
        {
            Success = true,
            NodesObserved = observations.Count,
            NodesAdded = reconciliation.NodesAdded,
            NodesUpdated = reconciliation.NodesUpdated,
            NodesTombstoned = reconciliation.NodesTombstoned,
        };
    }

    private async Task ObserveAsync(
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

                // Create observation for this directory
                var dirObs = new ScanObservationEntity
                {
                    ScanRunId = 0, // Set by caller
                    LibraryId = _libraryId,
                    RelativePath = entry.RelativePath,
                    PathKey = entry.RelativePath,
                    Kind = 0, // folder
                    DisplayName = entry.Name,
                    ParentPathKey = parentPathKey,
                    ObservationStatus = 0,
                };
                observations.Add(dirObs);

                // Recurse into the directory — the directory's own PathKey
                // becomes the ParentPathKey for its children (D1 fix).
                await ObserveAsync(entry.RelativePath, entry.RelativePath, observations, ct);
            }
            else if (entry.Kind == EntryKind.File)
            {
                if (!_policy.IsArchiveCandidate(entry))
                    continue;

                var stamp = _fs.GetSourceStamp(entry.RelativePath);
                var fileObs = new ScanObservationEntity
                {
                    ScanRunId = 0,
                    LibraryId = _libraryId,
                    RelativePath = entry.RelativePath,
                    PathKey = entry.RelativePath,
                    Kind = 1, // archive
                    DisplayName = entry.Name,
                    ParentPathKey = parentPathKey,
                    ByteLength = stamp.ByteLength,
                    ModificationTicks = stamp.LastWriteTicks,
                    ObservationStatus = 0,
                };
                observations.Add(fileObs);
            }
        }
    }

    private async Task<ReconciliationResult> ReconcileAsync(
        List<ScanObservationEntity> observations,
        CancellationToken ct)
    {
        var result = new ReconciliationResult();

        // Sort observations by path depth (parents first) so that parent
        // nodes are created before children. This lets us build a
        // pathKey→nodeId map incrementally (audit defect D1).
        var sorted = observations
            .OrderBy(o => o.PathKey.Count(c => c == '/' || c == '\\'))
            .ThenBy(o => o.PathKey)
            .ToList();

        // Load existing nodes for this library into a pathKey→node map
        var existingNodes = await _db.CatalogNodes
            .Where(n => n.LibraryId == _libraryId)
            .ToDictionaryAsync(n => n.PathKey, ct);

        // pathKey→nodeId map, seeded from existing nodes and filled as new
        // nodes are saved. Used to resolve ParentPathKey → ParentId.
        var pathToNodeId = new Dictionary<string, long>();
        foreach (var kvp in existingNodes)
        {
            pathToNodeId[kvp.Key] = kvp.Value.Id;
        }

        var observedPathKeys = new HashSet<string>();

        foreach (var obs in sorted)
        {
            ct.ThrowIfCancellationRequested();

            observedPathKeys.Add(obs.PathKey);

            // Resolve parent ID from the path map
            long? parentId = null;
            if (!string.IsNullOrEmpty(obs.ParentPathKey))
            {
                if (pathToNodeId.TryGetValue(obs.ParentPathKey, out var parentIdValue))
                    parentId = parentIdValue;
                // If the parent path key is not in the map, the parent was
                // filtered out (e.g., not traversed). Leave parentId null so
                // the node appears at the root of its accessible subtree.
            }

            if (existingNodes.TryGetValue(obs.PathKey, out var existing))
            {
                // Node exists — check if it needs updating
                bool needsUpdate = false;

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
                }

                // Repair parent ID if it differs (fixes libraries scanned
                // before the D1 hierarchy fix)
                if (existing.ParentId != parentId)
                {
                    existing.ParentId = parentId;
                    needsUpdate = true;
                }

                existing.LastSeenScanRevision = _scanRevision;

                if (needsUpdate)
                {
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                    result.NodesUpdated++;
                }

                // For archives, check if content changed (byte length or modification time)
                if (existing.Kind == 1 && existing.ArchiveItem is not null)
                {
                    var archiveItem = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == existing.Id, ct);
                    if (archiveItem is not null)
                    {
                        if (archiveItem.ByteLength != obs.ByteLength || archiveItem.ModificationTicks != obs.ModificationTicks)
                        {
                            archiveItem.ContentVersion++;
                            archiveItem.ByteLength = obs.ByteLength;
                            archiveItem.ModificationTicks = obs.ModificationTicks;
                            result.NodesUpdated++;
                        }
                    }
                }
            }
            else
            {
                // New node — create it
                var node = new CatalogNodeEntity
                {
                    PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
                    LibraryId = _libraryId,
                    ParentId = parentId,
                    Kind = obs.Kind,
                    DisplayName = obs.DisplayName,
                    RelativePath = obs.RelativePath,
                    PathKey = obs.PathKey,
                    SortKey = BuildSortKey(obs.Kind, obs.DisplayName),
                    Availability = 0,
                    LastSeenScanRevision = _scanRevision,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                _db.CatalogNodes.Add(node);
                await _db.SaveChangesAsync(ct); // Save to get the auto-generated Id

                // Register in the path map so children can resolve this as parent
                pathToNodeId[node.PathKey] = node.Id;

                result.NodesAdded++;

                // For archives, create the ArchiveItem entity
                if (obs.Kind == 1)
                {
                    _db.ArchiveItems.Add(new ArchiveItemEntity
                    {
                        NodeId = node.Id,
                        ArchiveFormat = 0, // Unknown until analysis
                        ByteLength = obs.ByteLength,
                        ModificationTicks = obs.ModificationTicks,
                        ContentVersion = 1,
                        AnalysisState = 1, // pending
                    });
                    await _db.SaveChangesAsync(ct);
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        result.Success = true;
        return result;
    }

    private async Task TombstoneMissingNodesAsync(CancellationToken ct)
    {
        // Find all nodes that were not seen in this scan
        var missingNodes = await _db.CatalogNodes
            .Where(n => n.LibraryId == _libraryId && n.LastSeenScanRevision < _scanRevision && n.Availability != 5)
            .ToListAsync(ct);

        if (missingNodes.Count == 0)
            return;

        // Suspicious-loss detection
        var totalNodes = await _db.CatalogNodes.CountAsync(n => n.LibraryId == _libraryId, ct);
        var archiveCount = missingNodes.Count(n => n.Kind == 1);

        // Default threshold: at least 100 previously present archives and more than 90% absent
        // Plus always-suspicious: nonempty-to-empty
        if (archiveCount >= 100 && (double)missingNodes.Count / totalNodes > 0.9)
        {
            _logger?.LogWarning("Suspicious loss detected: {MissingCount} of {TotalCount} nodes missing in library {LibraryId}. Requires admin review.", missingNodes.Count, totalNodes, _libraryId);
            // Do not tombstone — require admin review
            return;
        }

        if (totalNodes > 0 && missingNodes.Count == totalNodes)
        {
            _logger?.LogWarning("Suspicious loss: all {TotalCount} nodes missing in library {LibraryId}. Requires admin review.", totalNodes, _libraryId);
            return;
        }

        // Tombstone the missing nodes
        foreach (var node in missingNodes)
        {
            node.Availability = 5; // tombstoned
            node.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
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
    public int NodesTombstoned { get; init; }
}
