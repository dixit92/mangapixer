namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Library.Moves;
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
/// default, folder Content and (1.28.0) its declared facts - are MOVED to that folder N when all moved archives
/// agree on one live N, N has no row of that kind, and the moved archives are at
/// least 80% of T's former archives. Anything else stays on T and shows in the
/// review dashboard's "Missing folders" tab for a manual re-attach or delete.
/// Local only (no network); one query when nothing is stranded; ids-only logs.
///
/// 1.31.0 (cross-library moves): the old folder may be in ANOTHER library than the one scanned (the trees of every library
/// the ledger touches are loaded); folder favorites, view settings and cover choice move too; links follow
/// <see cref="MoveLinkRules"/> (an Auto link to the record the old folder was Confirmed to becomes Confirmed; a different
/// admin decision leaves a move conflict for the admin instead of silently keeping either).
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
    public IQueryable<long> StrandedFolderIds(long? libraryId = null) =>
        StrandedFolderIds(libraryId is { } lib ? [lib] : null);

    /// <summary>
    /// Tombstoned folders of the given libraries (or all) that still carry an admin metadata row - except one whose link
    /// waits in an open move conflict (it shows on the Move conflicts page instead).
    /// </summary>
    public IQueryable<long> StrandedFolderIds(IReadOnlyCollection<long>? libraryIds)
    {
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var folder = (int)CatalogNodeKind.Folder;
        var needsReview = (int)SeriesLinkState.NeedsReview;
        var open = (int)MoveConflictState.Open;
        var nodes = _db.CatalogNodes.Where(n => n.Kind == folder && n.Availability == tombstoned);
        if (libraryIds is not null)
            nodes = nodes.Where(n => libraryIds.Contains(n.LibraryId));
        return nodes
            .Where(n => _db.NodeSeriesLinks.Any(l => l.NodeId == n.Id && l.State != needsReview)
                || _db.FolderMetadataPrecedences.Any(p => p.NodeId == n.Id)
                || _db.FolderReaderDefaults.Any(r => r.NodeId == n.Id)
                || _db.FolderMetadataContents.Any(c => c.NodeId == n.Id)
                || _db.FolderCoverPreferences.Any(c => c.NodeId == n.Id)
                || _db.DeclaredFacts.Any(f => f.NodeId == n.Id))
            .Where(n => !_db.MoveConflicts.Any(c => c.State == open && c.Move!.FromNodeId == n.Id))
            .Select(n => n.Id);
    }

    /// <summary>
    /// Every removed folder carry-over looks at (1.31.0): the stranded ones plus those that only carry a user's favorite, a
    /// view setting or a cover choice; never one a move recognised earlier already handled.
    /// </summary>
    private IQueryable<long> CarryCandidateIds(IReadOnlyCollection<long> libraryIds)
    {
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var folder = (int)CatalogNodeKind.Folder;
        var extra = _db.CatalogNodes
            .Where(n => n.Kind == folder && n.Availability == tombstoned && libraryIds.Contains(n.LibraryId))
            .Where(n => _db.Favorites.Any(f => f.CatalogNodeId == n.Id)
                || _db.FolderViewSettings.Any(v => v.NodeId == n.Id)
                || _db.NodeCoverChoices.Any(c => c.NodeId == n.Id))
            .Select(n => n.Id);
        return StrandedFolderIds(libraryIds).Union(extra)
            .Where(id => !_db.NodeMoves.Any(m => m.FromNodeId == id));
    }

    /// <summary>Runs carry-over for one scan's ledger. Returns how many folders were carried.</summary>
    public Task<int> CarryAsync(long libraryId, IReadOnlyList<ScanMove> ledger, CancellationToken ct = default) =>
        CarryAsync(libraryId, ledger, pairedTombstones: null, ct);

    /// <summary>
    /// Runs carry-over for a ledger whose old parents may lie in other libraries (1.31.0). <paramref name="pairedTombstones"/>
    /// are old archives whose state was copied onto a new copy (a move recognised after the fact): they stay tombstoned but
    /// count as moved, not as left behind. Returns how many folders were carried.
    /// </summary>
    public async Task<int> CarryAsync(long libraryId, IReadOnlyList<ScanMove> ledger, IReadOnlySet<long>? pairedTombstones, CancellationToken ct = default)
    {
        if (ledger.Count == 0)
            return 0;
        var oldParents = ledger.Select(m => m.OldParentId).Distinct().ToList();
        var libraries = new HashSet<long> { libraryId };
        foreach (var chunk in oldParents.Chunk(500))
            libraries.UnionWith(await _db.CatalogNodes.Where(n => chunk.Contains(n.Id)).Select(n => n.LibraryId).Distinct().ToListAsync(ct));
        var libraryList = libraries.ToList();
        var stranded = await CarryCandidateIds(libraryList).ToListAsync(ct);
        if (stranded.Count == 0)
            return 0;

        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var nodes = await _db.CatalogNodes.AsNoTracking()
            .Where(n => libraryList.Contains(n.LibraryId))
            .Select(n => new TreeRow(n.Id, n.ParentId, n.Kind == (int)CatalogNodeKind.Folder, n.Availability != tombstoned))
            .ToDictionaryAsync(n => n.Id, ct);

        var strandedArchiveParents = nodes.Values
            .Where(r => !r.IsFolder && !r.Live && r.ParentId is not null && !(pairedTombstones?.Contains(r.Id) ?? false))
            .Select(r => r.ParentId!.Value).ToList();
        var carried = 0;
        foreach (var t in stranded)
        {
            var target = MapTarget(t, ledger, nodes, strandedArchiveParents);
            if (target is not { } n)
                continue;
            var moved = await MoveRowsAsync(t, n, ct, recordConflicts: true);
            if (moved.Any)
            {
                carried++;
                var targetLibrary = await _db.CatalogNodes.Where(x => x.Id == n).Select(x => x.LibraryId).FirstAsync(ct);
                await _audit.RecordAsync(AuditActions.MetadataLinkCarried, AuditResults.Success, actorUserName: null, ct: ct,
                    targetLibraryId: targetLibrary, targetItemId: n);
            }
        }
        if (carried > 0)
            _logger.LogInformation(LogEvents.Metadata.CarryOver, "Metadata carry-over for library {LibraryId}: {Carried} of {Stranded} folders carried ({Libraries} libraries)",
                libraryId, carried, stranded.Count, libraryList.Count);
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

    public sealed record MovedRows(bool Link, bool Precedence, bool ReaderDefault, bool Content, bool Declared = false,
        bool Favorites = false, bool ViewSettings = false, bool CoverChoice = false, bool LinkConflict = false, bool CoverPreference = false)
    {
        public bool Any => Link || Precedence || ReaderDefault || Content || Declared || Favorites || ViewSettings || CoverChoice || LinkConflict
            || CoverPreference;
    }

    /// <summary>
    /// Moves each of T's rows to N when N has none of that kind (a row N already has
    /// wins; T keeps the one that could not move). Needs review rows are dropped - the
    /// new folder is matched afresh.
    /// </summary>
    public async Task<MovedRows> MoveRowsAsync(long fromNodeId, long toNodeId, CancellationToken ct, bool recordConflicts = false)
    {
        var now = _time.GetUtcNow();
        var toLibrary = await _db.CatalogNodes.Where(n => n.Id == toNodeId).Select(n => n.LibraryId).FirstAsync(ct);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var (link, linkConflict, droppedRecord) = await ApplyLinkRulesAsync(fromNodeId, toNodeId, toLibrary, recordConflicts, now, ct);

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

        // 1.31.0: the folder's view settings and cover choice (admin rows) move like the others.
        var viewSettings = false;
        var fromView = await _db.FolderViewSettings.FirstOrDefaultAsync(v => v.NodeId == fromNodeId, ct);
        if (fromView is not null && !await _db.FolderViewSettings.AnyAsync(v => v.NodeId == toNodeId, ct))
        {
            fromView.NodeId = toNodeId;
            viewSettings = true;
        }

        var coverChoice = false;
        var fromCover = await _db.NodeCoverChoices.FirstOrDefaultAsync(c => c.NodeId == fromNodeId, ct);
        if (fromCover is not null && !await _db.NodeCoverChoices.AnyAsync(c => c.NodeId == toNodeId, ct))
        {
            fromCover.NodeId = toNodeId;
            fromCover.Version++;
            coverChoice = true;
        }

        // 1.32.0: the folder's cover preference (Web covers / File covers) moves like the reader default.
        var coverPreference = false;
        var fromCoverPreference = await _db.FolderCoverPreferences.FirstOrDefaultAsync(c => c.NodeId == fromNodeId, ct);
        if (fromCoverPreference is not null && !await _db.FolderCoverPreferences.AnyAsync(c => c.NodeId == toNodeId, ct))
        {
            fromCoverPreference.NodeId = toNodeId;
            coverPreference = true;
        }

        // Declared facts (1.28.0) move as one set: all of T's rows, only when N declares nothing of its own.
        var declared = await _db.DeclaredFacts.AnyAsync(f => f.NodeId == fromNodeId, ct)
            && !await _db.DeclaredFacts.AnyAsync(f => f.NodeId == toNodeId, ct);

        await _db.SaveChangesAsync(ct);
        if (declared)
        {
            await _db.DeclaredFacts.Where(f => f.NodeId == fromNodeId).ExecuteUpdateAsync(u => u
                .SetProperty(f => f.NodeId, toNodeId)
                .SetProperty(f => f.LibraryId, toLibrary)
                .SetProperty(f => f.UpdatedAt, now), ct);
        }

        // 1.31.0: each user's star follows the folder unless that user starred the new folder already.
        var starredNew = _db.Favorites.Where(f => f.CatalogNodeId == toNodeId).Select(f => f.UserId);
        var favorites = await _db.Favorites.Where(f => f.CatalogNodeId == fromNodeId && !starredNew.Contains(f.UserId))
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.CatalogNodeId, toNodeId), ct) > 0;
        await tx.CommitAsync(ct);

        if (droppedRecord is { } recordId)
            await RemoveOrphanRecordsAsync([recordId], ct);
        return new MovedRows(link, precedence, readerDefault, content, declared, favorites, viewSettings, coverChoice, linkConflict, coverPreference);
    }

    /// <summary>
    /// The old node's series link against the new node's (<see cref="MoveLinkRules"/>), for folders and archive works.
    /// Changes are tracked (saved by the caller) except the conflict row. Returns whether the old link now serves the new
    /// node, whether a conflict was recorded, and a record the old link referenced that may now be orphaned.
    /// </summary>
    public async Task<(bool Moved, bool Conflict, long? DroppedRecordId)> ApplyLinkRulesAsync(
        long fromNodeId, long toNodeId, long toLibrary, bool recordConflicts, DateTimeOffset now, CancellationToken ct)
    {
        var fromLink = await _db.NodeSeriesLinks.FirstOrDefaultAsync(l => l.NodeId == fromNodeId, ct);
        var toLink = fromLink is null ? null : await _db.NodeSeriesLinks.FirstOrDefaultAsync(l => l.NodeId == toNodeId, ct);
        var outcome = MoveLinkRules.Decide(
            fromLink is null ? null : new MoveLinkSnapshot((SeriesLinkState)fromLink.State, fromLink.RecordId),
            toLink is null ? null : new MoveLinkSnapshot((SeriesLinkState)toLink.State, toLink.RecordId));
        switch (outcome)
        {
            case MoveLinkOutcome.MoveOld:
                fromLink!.NodeId = toNodeId;
                fromLink.LibraryId = toLibrary;
                fromLink.UpdatedAt = now;
                return (true, false, null);
            case MoveLinkOutcome.ReplaceNewWithOld:
                _db.NodeSeriesLinks.Remove(toLink!);
                await _db.SaveChangesAsync(ct); // NodeId is unique: free it before the old link takes it
                fromLink!.NodeId = toNodeId;
                fromLink.LibraryId = toLibrary;
                fromLink.UpdatedAt = now;
                return (true, false, toLink!.RecordId);
            case MoveLinkOutcome.PromoteNew:
                toLink!.State = (int)SeriesLinkState.Confirmed;
                toLink.MatchMethod = fromLink!.MatchMethod;
                toLink.MatchScore = fromLink.MatchScore;
                toLink.UpdatedAt = now;
                _db.NodeSeriesLinks.Remove(fromLink);
                return (true, false, null);
            case MoveLinkOutcome.DropOld:
                _db.NodeSeriesLinks.Remove(fromLink!);
                return (false, false, fromLink!.State == (int)SeriesLinkState.NeedsReview ? null : fromLink.RecordId);
            case MoveLinkOutcome.Conflict when recordConflicts:
                var moveId = await MoveConflictRecorder.EnsureMoveAsync(_db, fromNodeId, toNodeId, now, ct);
                await MoveConflictRecorder.AddAsync(_db, moveId, null, MoveConflictKind.SeriesLink, now, ct);
                return (false, true, null);
            default:
                return (false, false, null);
        }
    }

    /// <summary>Removes records no link references any more (as on unlink) and tells the removal handlers.</summary>
    public async Task RemoveOrphanRecordsAsync(IReadOnlyCollection<long> recordIds, CancellationToken ct)
    {
        var orphans = await _db.MetadataRecords.Where(r => recordIds.Contains(r.Id) && !_db.NodeSeriesLinks.Any(l => l.RecordId == r.Id))
            .Select(r => r.Id).ToListAsync(ct);
        if (orphans.Count == 0)
            return;
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

    /// <summary>Manual re-attach of a Missing folders row onto a live folder - of any library since 1.31.1.</summary>
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
        // 1.31.1: another library is allowed (a series moved between libraries whose archives were not recognised as moved);
        // MoveRowsAsync rewrites the denormalised library ids.

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
            Declared = moved.Declared,
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
        await _db.FolderCoverPreferences.Where(c => c.NodeId == node.Id).ExecuteDeleteAsync(ct);
        await _db.DeclaredFacts.Where(f => f.NodeId == node.Id).ExecuteDeleteAsync(ct);
        await _db.MetadataMatchCandidates.Where(c => c.NodeId == node.Id).ExecuteDeleteAsync(ct);
        // Records no link references any more go, as on unlink.
        await RemoveOrphanRecordsAsync(recordIds, ct);
        await _audit.RecordAsync(AuditActions.MetadataMissingDelete, AuditResults.Success, actor, ct: ct,
            targetLibraryId: node.LibraryId, targetItemId: node.Id);
        return true;
    }
}
