namespace com.lifepixer.mangapixer.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>Writes <c>node_moves</c> and <c>move_conflicts</c> rows (1.31.0); idempotent, so a repeated pass adds nothing.</summary>
public static class MoveConflictRecorder
{
    /// <summary>The move row of an old node, created when missing. Returns its id.</summary>
    public static async Task<long> EnsureMoveAsync(MangaPixerDbContext db, long fromNodeId, long toNodeId, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.NodeMoves.Where(m => m.FromNodeId == fromNodeId).Select(m => (long?)m.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id)
            return id;
        var kind = await db.CatalogNodes.Where(n => n.Id == fromNodeId).Select(n => n.Kind).FirstAsync(ct);
        var move = new NodeMoveEntity { FromNodeId = fromNodeId, ToNodeId = toNodeId, Kind = kind, CreatedAt = now };
        db.NodeMoves.Add(move);
        await db.SaveChangesAsync(ct);
        return move.Id;
    }

    /// <summary>Adds an open conflict unless the move already has one of this kind for this user.</summary>
    public static async Task AddAsync(MangaPixerDbContext db, long moveId, long? userId, MoveConflictKind kind, DateTimeOffset now, CancellationToken ct)
    {
        var k = (int)kind;
        if (await db.MoveConflicts.AnyAsync(c => c.MoveId == moveId && c.UserId == userId && c.Kind == k, ct))
            return;
        db.MoveConflicts.Add(new MoveConflictEntity
        {
            MoveId = moveId,
            UserId = userId,
            Kind = k,
            State = (int)MoveConflictState.Open,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }
}
