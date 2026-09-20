namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Per-user favorites writes (1.21.0): star / unstar a catalog node. Reads (the
/// favorites list and the IsFavorite projection into browse/search) live in
/// <see cref="CatalogBrowseService"/> so all node-DTO production and its cover /
/// read-state enrichment stay in one place; this service owns only the mutations.
///
/// Authorization: favoriting is gated on ACCESS to the node's library
/// (<see cref="LibraryAuthorizationService.GetAccessibleLibraryIdsAsync"/>), exactly
/// like direct item access — a user may favorite anything they can open, including a
/// node in a library they have marked Private (Private only hides listings, never
/// blocks access). Listing visibility (Incognito) is applied at read time.
/// </summary>
public sealed class FavoritesService
{
    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _auth;

    public FavoritesService(MangaPixerDbContext db, LibraryAuthorizationService auth)
    {
        _db = db;
        _auth = auth;
    }

    public enum FavoriteResult
    {
        /// <summary>The favorite exists after the call (created now or already present).</summary>
        Ok,

        /// <summary>The node does not exist or is not accessible to this user.</summary>
        NodeNotFound,
    }

    /// <summary>
    /// Adds a favorite for <paramref name="nodePublicId"/>. Idempotent: a second call is
    /// a no-op that still returns <see cref="FavoriteResult.Ok"/>. Returns
    /// <see cref="FavoriteResult.NodeNotFound"/> when the node is unknown or the user
    /// cannot access its library (so existence is not leaked).
    /// </summary>
    public async Task<FavoriteResult> AddFavoriteAsync(long userId, string nodePublicId, CancellationToken ct = default)
    {
        var node = await _db.CatalogNodes
            .FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null)
            return FavoriteResult.NodeNotFound;

        var accessible = await _auth.GetAccessibleLibraryIdsAsync(userId, ct);
        if (!accessible.Contains(node.LibraryId))
            return FavoriteResult.NodeNotFound;

        var exists = await _db.Favorites
            .AnyAsync(f => f.UserId == userId && f.CatalogNodeId == node.Id, ct);
        if (exists)
            return FavoriteResult.Ok;

        _db.Favorites.Add(new FavoriteEntity
        {
            UserId = userId,
            CatalogNodeId = node.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent add — the unique (UserId, CatalogNodeId)
            // index rejected the duplicate. The desired end state (favorited) holds, so
            // the toggle is still idempotent.
        }

        return FavoriteResult.Ok;
    }

    /// <summary>
    /// Removes the favorite for <paramref name="nodePublicId"/>. Idempotent: removing a
    /// node that is not favorited (or unknown) still returns
    /// <see cref="FavoriteResult.Ok"/> — DELETE is defined by the end state, not by
    /// whether a row was present.
    /// </summary>
    public async Task<FavoriteResult> RemoveFavoriteAsync(long userId, string nodePublicId, CancellationToken ct = default)
    {
        var node = await _db.CatalogNodes
            .FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null)
            return FavoriteResult.Ok;

        var favorite = await _db.Favorites
            .FirstOrDefaultAsync(f => f.UserId == userId && f.CatalogNodeId == node.Id, ct);
        if (favorite is null)
            return FavoriteResult.Ok;

        _db.Favorites.Remove(favorite);
        await _db.SaveChangesAsync(ct);
        return FavoriteResult.Ok;
    }
}
