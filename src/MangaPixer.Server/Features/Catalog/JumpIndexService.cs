using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace com.lifepixer.mangapixer.Server.Features.Catalog;

/// <summary>
/// The jump index of one listed level (1.4.0 library root; 1.41.0 any folder, both name-sort directions, smart chips).
///
/// Reads the live rows of the level the browse endpoint lists for <c>parentId</c> (one query, ordered by the persisted
/// <c>SortKey</c>) and hands them to <see cref="JumpIndexBuilder"/>: letters, or round number ranges when the names number their
/// chapters / volumes behind a common prefix. Every bucket is one contiguous run of the listing, and its <c>FirstCursor</c> is the
/// raw <c>SortKey</c> the browse name sort (same direction) honours to land on the bucket's first row. Inside a folder whose Volumes
/// view the viewer sees (the same check as browse, with the same <c>group</c> override), the index is empty: that view pages by
/// volume, not by <c>SortKey</c>.
///
/// Kept separate from <c>CatalogBrowseService</c>, which owns the main catalog-browsing surface.
/// </summary>
public sealed class JumpIndexService
{
    private readonly MangaPixerDbContext _db;
    private readonly VolumeEntryService _volumes;

    public JumpIndexService(MangaPixerDbContext db, VolumeEntryService? volumes = null)
    {
        _db = db;
        _volumes = volumes ?? new VolumeEntryService(db);
    }

    /// <summary>The ascending jump index of a library's root level (the 1.4.0 call).</summary>
    public Task<JumpIndexDto> GetJumpIndexAsync(long userId, long libraryId, CancellationToken ct = default) =>
        GetJumpIndexAsync(userId, libraryId, parentId: null, descending: false, group: null, ct);

    /// <summary>
    /// Computes the jump index of the children of <paramref name="parentId"/> (the library root when null) for the name sort in the
    /// given direction. Authorization is enforced: an inaccessible library yields an empty index. The caller has checked that the
    /// parent belongs to the library.
    /// </summary>
    public async Task<JumpIndexDto> GetJumpIndexAsync(
        long userId,
        long libraryId,
        long? parentId,
        bool descending,
        string? group,
        CancellationToken ct = default)
    {
        var direction = descending ? "desc" : "asc";

        // Authorization - same rule as before 1.41.0: accessible libraries only.
        var accessibleLibs = await GetAccessibleLibraryIdsAsync(userId, ct);
        if (!accessibleLibs.Contains(libraryId))
            return new JumpIndexDto { LibraryId = "", Buckets = [], Direction = direction };

        string? parentPublicId = null;
        string? containerName = null;
        if (parentId is { } folderId)
        {
            var parent = await _db.CatalogNodes.AsNoTracking()
                .Where(n => n.Id == folderId)
                .Select(n => new { n.PublicId, n.DisplayName })
                .FirstOrDefaultAsync(ct);
            parentPublicId = parent?.PublicId;
            containerName = parent?.DisplayName;

            // The Volumes view (1.29.0) takes over a folder's name sort with volume cursors: no rail there.
            var view = await _volumes.GetEntriesAsync(folderId, ct);
            if (view is { Available: true } && view.LibraryId == libraryId && await _volumes.IsActiveAsync(userId, view, group, ct))
                return new JumpIndexDto { LibraryId = "", ParentId = parentPublicId, Buckets = [], Direction = direction };
        }

        // The level browse lists: live children of the parent (or the root), in persisted SortKey (ordinal) order.
        var query = _db.CatalogNodes.AsNoTracking()
            .Where(n => n.LibraryId == libraryId)
            .Where(n => n.Availability != (int)CatalogNodeAvailability.Tombstoned);
        query = parentId is null ? query.Where(n => n.ParentId == null) : query.Where(n => n.ParentId == parentId);
        var rows = await query
            .OrderBy(n => n.SortKey)
            .Select(n => new { n.PublicId, n.Kind, n.DisplayName, n.SortKey })
            .ToListAsync(ct);

        // Kept in the DATABASE order: the browse keyset (`SortKey > cursor` / `< cursor`) compares in SQL too, so the runs the
        // builder forms are exactly the runs the browse pages walk.
        var jumpRows = rows
            .Select(r => new JumpRow(r.PublicId, (CatalogNodeKind)r.Kind, r.DisplayName, r.SortKey))
            .ToList();
        var rail = JumpIndexBuilder.Build(jumpRows, descending, containerName);

        var libId = await _db.Libraries
            .Where(l => l.Id == libraryId)
            .Select(l => l.PublicId)
            .FirstOrDefaultAsync(ct);

        return new JumpIndexDto
        {
            LibraryId = libId ?? "",
            ParentId = parentPublicId,
            Direction = direction,
            Mode = rail.Mode,
            Buckets = rail.Buckets.Select(b => new JumpIndexBucketDto
            {
                Label = b.Label,
                Count = b.Count,
                FirstCursor = b.FirstCursor,
                FirstNodeId = b.FirstNodeId,
                Range = b.Range,
            }).ToList(),
        };
    }

    private async Task<List<long>> GetAccessibleLibraryIdsAsync(long userId, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
            return [];

        if (user.IsAdmin)
            return await _db.Libraries.Select(l => l.Id).ToListAsync(ct);

        return await _db.LibraryGrants
            .Where(g => g.UserId == userId)
            .Select(g => g.LibraryId)
            .ToListAsync(ct);
    }
}
