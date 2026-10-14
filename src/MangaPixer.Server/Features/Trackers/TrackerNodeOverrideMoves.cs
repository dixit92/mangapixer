namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The users' tracker sync switches (<c>tracker_node_overrides</c>, owner Q3) follow a moved folder or archive (1.43.0), like favorites:
/// each user's switch moves to the new node unless that user already set one there (the newer one wins). A lost "off" switch would
/// silently resume syncing a series the user turned off, so the switch must not stay behind on the tombstone.
/// </summary>
public static class TrackerNodeOverrideMoves
{
    /// <summary>Moves the switches of <paramref name="fromNodeId"/> to <paramref name="toNodeId"/>; returns how many moved.</summary>
    public static Task<int> MoveAsync(MangaPixerDbContext db, long fromNodeId, long toNodeId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var setOnNew = db.TrackerNodeOverrides.Where(o => o.NodeId == toNodeId).Select(o => o.UserId);
        return db.TrackerNodeOverrides.Where(o => o.NodeId == fromNodeId && !setOnNew.Contains(o.UserId))
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.NodeId, toNodeId), ct);
    }
}
