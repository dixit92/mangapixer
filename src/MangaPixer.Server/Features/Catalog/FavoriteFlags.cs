namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Batched favorites flag for cards built outside <see cref="CatalogBrowseService"/> (the Home
/// rows, 1.28.0): ONE query (favorites joined to catalog_nodes, projected to public ids)
/// whatever the card count - the same projection browse's <c>ApplyFavoritesAsync</c> uses.
/// </summary>
public static class FavoriteFlags
{
    /// <summary>The public ids among <paramref name="publicIds"/> that <paramref name="userId"/> has starred.</summary>
    public static async Task<HashSet<string>> StarredAsync(
        MangaPixerDbContext db, long userId, IEnumerable<string> publicIds, CancellationToken ct = default)
    {
        var ids = publicIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        return (await db.Favorites
            .Where(f => f.UserId == userId)
            .Join(db.CatalogNodes, f => f.CatalogNodeId, n => n.Id, (f, n) => n.PublicId)
            .Where(pid => ids.Contains(pid))
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
    }
}
