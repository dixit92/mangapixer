namespace com.lifepixer.mangapixer.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Scanning;

/// <summary>
/// Tombstones move recognition still needs (1.31.0), which the trash must not purge whatever their age:
/// 1. the old node of a move with an OPEN conflict (its rows are the "old state" the admin may still choose);
/// 2. a tombstoned archive with a signature, not yet handled, while a live archive of the same size waits for analysis (it
///    may be the moved copy; conservative - it may hold a little more than needed, which only delays a purge).
/// Folder ancestors of held nodes are the trash's concern (parents are deleted only after their children).
/// </summary>
public sealed class MoveTombstoneHolds : ITombstoneHolds
{
    private readonly MangaPixerDbContext _db;

    public MoveTombstoneHolds(MangaPixerDbContext db) => _db = db;

    public IQueryable<long> HeldNodeIds()
    {
        var open = (int)MoveConflictState.Open;
        var tombstoned = MoveEvidence.Tombstoned;
        var conflicts = _db.MoveConflicts.Where(c => c.State == open).Select(c => c.Move!.FromNodeId);
        var waiting = _db.CatalogNodes
            .Where(n => n.Kind == 1 && n.Availability == tombstoned && !_db.NodeMoves.Any(m => m.FromNodeId == n.Id))
            .Join(_db.ArchiveItems.Where(a => a.ContentSignature != null), n => n.Id, a => a.NodeId, (n, a) => new { n.Id, a.ByteLength })
            .Where(t => _db.ArchiveItems.Any(p => p.ByteLength == t.ByteLength && p.AnalysisState == 1
                && _db.CatalogNodes.Any(l => l.Id == p.NodeId && l.Availability != tombstoned)))
            .Select(t => t.Id);
        return conflicts.Concat(waiting);
    }
}
