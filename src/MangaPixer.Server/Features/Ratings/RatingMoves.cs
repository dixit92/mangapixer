namespace com.lifepixer.mangapixer.Server.Features.Ratings;

using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The users' ratings (<c>user_ratings</c>, 1.52.0 lane R) follow a moved folder or archive like favorites: each user's rating moves to the
/// new node unless that user already rated the new node (that newer rating wins, and the old one goes with the tombstone).
/// </summary>
public static class RatingMoves
{
    /// <summary>Moves the ratings of <paramref name="fromNodeId"/> to <paramref name="toNodeId"/>; returns how many moved.</summary>
    public static Task<int> MoveAsync(MangaPixerDbContext db, long fromNodeId, long toNodeId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var ratedNew = db.UserRatings.Where(r => r.CatalogNodeId == toNodeId).Select(r => r.UserId);
        return db.UserRatings.Where(r => r.CatalogNodeId == fromNodeId && !ratedNew.Contains(r.UserId))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.CatalogNodeId, toNodeId), ct);
    }
}
