namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Scanning;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Folder-rename carry-over (metadata stage 2, owner decision 11). Archives keep
/// their node id when a folder is renamed or moved (content signature), but the
/// FOLDER is a new node, so its admin rows would be stranded on the tombstone.
/// After a scan, the moved-archive ledger tells where each removed folder T went:
/// every moved archive maps T to the folder at the same relative depth above its new
/// location. T's rows - link or Don't match, source precedence, folder reader
/// default and folder Content - are MOVED to that folder N when all moved archives
/// agree on one live N, N has no row of that kind, and the moved archives are at
/// least 80% of T's former archives. Anything else stays on T and shows in the
/// review dashboard's "Missing folders" tab for a manual re-attach or delete.
/// Local only (no network); one query when nothing is stranded; ids-only logs.
/// </summary>
public sealed class MetadataCarryOverService
{
    public const double MinMovedShare = 0.8;

    private readonly MangaPixerDbContext _db;
    private readonly AuditService _audit;
    private readonly TimeProvider _time;
    private readonly IEnumerable<IMetadataRecordRemovedHandler> _removedHandlers;
    private readonly ILogger<MetadataCarryOverService> _logger;

    public MetadataCarryOverService(
        MangaPixerDbContext db,
        AuditService audit,
        TimeProvider time,
        IEnumerable<IMetadataRecordRemovedHandler> removedHandlers,
        ILogger<MetadataCarryOverService> logger)
    {
        _db = db;
        _audit = audit;
        _time = time;
        _removedHandlers = removedHandlers;
        _logger = logger;
    }

    /// <summary>Tombstoned folders of a library (or all) that still carry an admin metadata row.</summary>
    public IQueryable<long> StrandedFolderIds(long? libraryId = null)
    {
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var folder = (int)CatalogNodeKind.Folder;
        var needsReview = (int)SeriesLinkState.NeedsReview;
        var nodes = _db.CatalogNodes.Where(n => n.Kind == folder && n.Availability == tombstoned);
        if (libraryId is { } lib)
            nodes = nodes.Where(n => n.LibraryId == lib);
        return nodes
            .Where(n => _db.NodeSeriesLinks.Any(l => l.NodeId == n.Id && l.State != needsReview)
                || _db.FolderMetadataPrecedences.Any(p => p.NodeId == n.Id)
                || _db.FolderReaderDefaults.Any(r => r.NodeId == n.Id)
                || _db.FolderMetadataContents.Any(c => c.NodeId == n.Id))
            .Select(n => n.Id);
    }

    /// <summary>Runs carry-over for one scan's ledger. Returns how many folders were carried.</summary>
    public async Task<int> CarryAsync(long libraryId, IReadOnlyList<ScanMove> ledger, CancellationToken ct = default)
    {
        if (ledger.Count == 0)
            return 0;
        var stranded = await StrandedFolderIds(libraryId).ToListAsync(ct);
        if (stranded.Count == 0)
            return 0;

        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var nodes = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.LibraryId == libraryId)
            .Select(n => new TreeRow(n.Id, n.ParentId, n.Kind == (int)CatalogNodeKind.Folder, n.Availability != tombstoned))
            .ToDictionaryAsync(n => n.Id, ct);

        var strandedArchiveParents = nodes.Values.Where(r => !r.IsFolder && !r.Live && r.ParentId is not null)
            .Select(r => r.ParentId!.Value).ToList();
        var carried = 0;
        foreach (var t in stranded)
        {
            var target = MapTarget(t, ledger, nodes, strandedArchiveParents);
            if (target is not { } n)
                continue;
            var moved = await MoveRowsAsync(t, n, ct);
            if (moved.Any)
            {
                carried++;
                await _audit.RecordAsync(AuditActions.MetadataLinkCarried, AuditResults.Success, actorUserName: null, ct: ct,
                    targetLibraryId: libraryId, targetItemId: n);
            }
        }
        if (carried > 0)
            _logger.LogInformation(LogEvents.Metadata.CarryOver, "Metadata carry-over for library {LibraryId}: {Carried} of {Stranded} folders carried",
                libraryId, carried, stranded.Count);
        return carried;
    }

    public readonly record struct TreeRow(long Id, long? ParentId, bool IsFolder, bool Live);

    /// <summary>
    /// Where the removed folder <paramref name="t"/> went, or null when the moved
    /// archives disagree, too few of T's archives moved, or the target is not a
    /// live folder. Pure over the given rows (unit-tested).
    /// </summary>
    public static long? MapTarget(long t, IReadOnlyList<ScanMove> ledger, IReadOnlyDictionary<long, TreeRow> nodes,
        IReadOnlyList<long>? strandedArchiveParents = null)
    {
        long? target = null;
        var movedCount = 0;
        foreach (var move in ledger)
        {
            // Relative depth of the OLD parent below T (0 = directly in T); -1 = not under T.
            var depth = DepthBelow(move.OldParentId, t, nodes);
            if (depth < 0)
                continue;
            if (!nodes.TryGetValue(move.ArchiveNodeId, out var archive) || archive.ParentId is not { } newParent)
                return null;
            var mapped = (long?)newParent;
            for (var i = 0; i < depth && mapped is { } m; i++)
                mapped = nodes.TryGetValue(m, out var row) ? row.ParentId : null;
            if (mapped is not { } candidate || (target is { } existing && existing != candidate))
                return null; // Split folder: no single target.
            target = candidate;
            movedCount++;
        }
        if (target is not { } n || !nodes.TryGetValue(n, out var targetRow) || !targetRow.IsFolder || !targetRow.Live || n == t)
            return null;

        // Archives still stranded (tombstoned) anywhere below T did not follow.
        strandedArchiveParents ??= nodes.Values.Where(r => !r.IsFolder && !r.Live && r.ParentId is not null).Select(r => r.ParentId!.Value).ToList();
        var remaining = strandedArchiveParents.Count(p => DepthBelow(p, t, nodes) >= 0);
        return movedCount >= MinMovedShare * (movedCount + remaining) ? n : null;
    }

    /// <summary>How many levels <paramref name="folderId"/> is below <paramref name="ancestorId"/> (0 = same), or -1.</summary>
    private static int DepthBelow(long folderId, long ancestorId, IReadOnlyDictionary<long, TreeRow> nodes)
    {
        long? current = folderId;
        for (var depth = 0; current is { } id && depth <= SeriesInfoResolver.MaxWalkDepth; depth++)
        {
            if (id == ancestorId)
                return depth;
            current = nodes.TryGetValue(id, out var row) ? row.ParentId : null;
        }
        return -1;
    }

    public sealed record MovedRows(bool Link, bool Precedence, bool ReaderDefault, bool Content)
    {
        public bool Any => Link || Precedence || ReaderDefault || Content;
    }

    /// <summary>
    /// Moves each of T's rows to N when N has none of that kind (a row N already has
    /// wins; T keeps the one that could not move). Needs review rows are dropped - the
    /// new folder is matched afresh.
    /// </summary>
    public async Task<MovedRows> MoveRowsAsync(long fromNodeId, long toNodeId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var toLibrary = await _db.CatalogNodes.Where(n => n.Id == toNodeId).Select(n => n.LibraryId).FirstAsync(ct);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var link = false;
        var fromLink = await _db.NodeSeriesLinks.FirstOrDefaultAsync(l => l.NodeId == fromNodeId, ct);
        if (fromLink is { State: (int)SeriesLinkState.NeedsReview })
        {
            _db.NodeSeriesLinks.Remove(fromLink);
        }
        else if (fromLink is not null && !await _db.NodeSeriesLinks.AnyAsync(l => l.NodeId == toNodeId, ct))
        {
            fromLink.NodeId = toNodeId;
            fromLink.LibraryId = toLibrary;
            fromLink.UpdatedAt = now;
            link = true;
        }

        var precedence = false;
        var fromPrecedence = await _db.FolderMetadataPrecedences.FirstOrDefaultAsync(p => p.NodeId == fromNodeId, ct);
        if (fromPrecedence is not null && !await _db.FolderMetadataPrecedences.AnyAsync(p => p.NodeId == toNodeId, ct))
        {
            fromPrecedence.NodeId = toNodeId;
            precedence = true;
        }

        var readerDefault = false;
        var fromReader = await _db.FolderReaderDefaults.FirstOrDefaultAsync(r => r.NodeId == fromNodeId, ct);
        if (fromReader is not null && !await _db.FolderReaderDefaults.AnyAsync(r => r.NodeId == toNodeId, ct))
        {
            fromReader.NodeId = toNodeId;
            readerDefault = true;
        }

        var content = false;
        var fromContent = await _db.FolderMetadataContents.FirstOrDefaultAsync(c => c.NodeId == fromNodeId, ct);
        if (fromContent is not null && !await _db.FolderMetadataContents.AnyAsync(c => c.NodeId == toNodeId, ct))
        {
            fromContent.NodeId = toNodeId;
            content = true;
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new MovedRows(link, precedence, readerDefault, content);
    }

    /// <summary>Manual re-attach of a Missing folders row onto a live folder of the same library.</summary>
    public async Task<(string? Error, MetadataReattachResultDto? Result)> ReattachAsync(
        string fromPublicId, string toPublicId, string? actor, CancellationToken ct = default)
    {
        var from = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == fromPublicId, ct);
        var to = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == toPublicId, ct);
        if (from is null || from.Availability != (int)CatalogNodeAvailability.Tombstoned || from.Kind != (int)CatalogNodeKind.Folder)
            return ("not_found", null);
        if (to is null || to.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return ("target_not_found", null);
        if (to.Kind != (int)CatalogNodeKind.Folder)
            return ("not_a_folder", null);
        if (to.LibraryId != from.LibraryId)
            return ("other_library", null);

        var moved = await MoveRowsAsync(from.Id, to.Id, ct);
        await _audit.RecordAsync(AuditActions.MetadataReattach, moved.Any ? AuditResults.Success : "nothing_moved", actor, ct: ct,
            targetLibraryId: to.LibraryId, targetItemId: to.Id);
        return (null, new MetadataReattachResultDto
        {
            NodeId = from.PublicId,
            TargetNodeId = to.PublicId,
            Link = moved.Link,
            Precedence = moved.Precedence,
            ReaderDefault = moved.ReaderDefault,
            Content = moved.Content,
        });
    }

    /// <summary>Deletes every admin metadata row left on a removed folder.</summary>
    public async Task<bool> DeleteMissingAsync(string nodePublicId, string? actor, CancellationToken ct = default)
    {
        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null || node.Availability != (int)CatalogNodeAvailability.Tombstoned)
            return false;
        var recordIds = await _db.NodeSeriesLinks.Where(l => l.NodeId == node.Id && l.RecordId != null).Select(l => l.RecordId!.Value).ToListAsync(ct);
        await _db.NodeSeriesLinks.Where(l => l.NodeId == node.Id).ExecuteDeleteAsync(ct);
        await _db.FolderMetadataPrecedences.Where(p => p.NodeId == node.Id).ExecuteDeleteAsync(ct);
        await _db.FolderReaderDefaults.Where(r => r.NodeId == node.Id).ExecuteDeleteAsync(ct);
        await _db.FolderMetadataContents.Where(c => c.NodeId == node.Id).ExecuteDeleteAsync(ct);
        await _db.MetadataMatchCandidates.Where(c => c.NodeId == node.Id).ExecuteDeleteAsync(ct);
        // Records no link references any more go, as on unlink.
        var orphans = await _db.MetadataRecords.Where(r => recordIds.Contains(r.Id) && !_db.NodeSeriesLinks.Any(l => l.RecordId == r.Id))
            .Select(r => r.Id).ToListAsync(ct);
        if (orphans.Count > 0)
        {
            await _db.MetadataRecords.Where(r => orphans.Contains(r.Id)).ExecuteDeleteAsync(ct);
            foreach (var handler in _removedHandlers)
            {
                try { await handler.OnRecordsRemovedAsync(orphans, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(LogEvents.Metadata.CarryOverFailed, "Record removal handler failed: {Error}", ex.GetType().Name);
                }
            }
        }
        await _audit.RecordAsync(AuditActions.MetadataMissingDelete, AuditResults.Success, actor, ct: ct,
            targetLibraryId: node.LibraryId, targetItemId: node.Id);
        return true;
    }
}
