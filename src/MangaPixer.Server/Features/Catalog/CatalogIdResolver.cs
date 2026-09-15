namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Resolves opaque public IDs (random base36 strings assigned at creation)
/// to internal database row IDs. Replaces the incorrect use of
/// <c>OpaqueId.Decode</c> as a public→internal ID resolver (audit defect D5/D29).
///
/// Public IDs are random base36 strings unrelated to the row ID. Decoding
/// them as if they were encoded row IDs produced wrong internal IDs, causing
/// browse, breadcrumbs, neighbors, search, and reading endpoints to return
/// empty results or 404 for valid public IDs.
/// </summary>
public sealed class CatalogIdResolver
{
    private readonly MangaPixerDbContext _db;

    public CatalogIdResolver(MangaPixerDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Resolves a catalog node's public ID to the entity. Returns null if
    /// the public ID does not match any node.
    /// </summary>
    public async Task<CatalogNodeEntity?> ResolveNodeAsync(string publicId, CancellationToken ct = default)
    {
        return await _db.CatalogNodes.FirstOrDefaultAsync(n => n.PublicId == publicId, ct);
    }

    /// <summary>
    /// Resolves a library's public ID to the entity. Returns null if the
    /// public ID does not match any library.
    /// </summary>
    public async Task<LibraryEntity?> ResolveLibraryAsync(string publicId, CancellationToken ct = default)
    {
        return await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == publicId, ct);
    }
}
