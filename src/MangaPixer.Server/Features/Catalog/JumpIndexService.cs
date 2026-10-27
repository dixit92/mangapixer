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
    private readonly CatalogBrowseService _browse;
    private readonly TimeProvider _time;

    public JumpIndexService(MangaPixerDbContext db, VolumeEntryService? volumes = null, CatalogBrowseService? browse = null,
        TimeProvider? time = null)
    {
        _db = db;
        _volumes = volumes ?? new VolumeEntryService(db);
        // 1.49.0: the filtered rail lists the rows browse lists - through browse's own level-filter pass.
        _browse = browse ?? new CatalogBrowseService(db, new Auth.LibraryAuthorizationService(db));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The ascending jump index of a library's root level (the 1.4.0 call).</summary>
    public Task<JumpIndexDto> GetJumpIndexAsync(long userId, long libraryId, CancellationToken ct = default) =>
        GetJumpIndexAsync(userId, libraryId, parentId: null, descending: false, group: null, ct);

    /// <summary>
    /// Computes the jump index of the children of <paramref name="parentId"/> (the library root when null) for the name sort in the
    /// given direction. Authorization is enforced: an inaccessible library yields an empty index. The caller has checked that the
    /// parent belongs to the library.
    /// </summary>
    public Task<JumpIndexDto> GetJumpIndexAsync(
        long userId,
        long libraryId,
        long? parentId,
        bool descending,
        string? group,
        CancellationToken ct = default) =>
        GetJumpIndexAsync(userId, libraryId, parentId, descending, group, incognito: false, ct);

    /// <summary>
    /// As above; 1.48.0 (lane A, owner decision 6b): under <paramref name="incognito"/> the VISIBLE libraries apply, as browse - the
    /// viewer's own Private library answers an empty rail, exactly as an inaccessible one (its browse is empty too).
    /// </summary>
    public Task<JumpIndexDto> GetJumpIndexAsync(
        long userId,
        long libraryId,
        long? parentId,
        bool descending,
        string? group,
        bool incognito,
        CancellationToken ct = default) =>
        GetJumpIndexAsync(userId, libraryId, parentId, descending, group, incognito, LevelFilter.None, "name", 0, ct);

    /// <summary>
    /// 1.49.0 (lane J, owner decisions 10 and 14): the index of the level browse lists under <paramref name="filter"/> (read state, hide
    /// empty, favorites only, Completion - the same pass browse applies, <see cref="CatalogBrowseService.ApplyLevelFiltersAsync"/>), so the
    /// chips cover the FILTERED rows and each cursor is the key of the previous row of the filtered list. <paramref name="sort"/> is the
    /// browse sort: <c>name</c> (the default; unknown values too) gets letters / numbers and honours <paramref name="descending"/>;
    /// <c>recentlyAdded</c> / <c>recentlyUpdated</c> get time chips (newest first, browse's date cursors, boundaries in the viewer's day by
    /// <paramref name="utcOffsetMinutes"/>); <c>recentlyRead</c> has no rail (its cursor is an offset into an in-memory order).
    /// </summary>
    public async Task<JumpIndexDto> GetJumpIndexAsync(
        long userId,
        long libraryId,
        long? parentId,
        bool descending,
        string? group,
        bool incognito,
        LevelFilter filter,
        string sort,
        int utcOffsetMinutes,
        CancellationToken ct = default)
    {
        sort = sort is JumpIndexBuilder.RecentlyAddedSort or JumpIndexBuilder.RecentlyUpdatedSort or "recentlyRead" ? sort : "name";
        var byDate = sort != "name";
        // The date sorts are newest-first only (browse ignores the direction there).
        var direction = byDate ? "desc" : descending ? "desc" : "asc";

        // Authorization - accessible libraries (the rule before 1.41.0), minus the viewer's Private set under Incognito (1.48.0).
        var accessibleLibs = await GetAccessibleLibraryIdsAsync(userId, ct);
        if (incognito && accessibleLibs.Contains(libraryId)
            && await _db.PrivateLibraries.AsNoTracking().AnyAsync(p => p.UserId == userId && p.LibraryId == libraryId, ct))
            return new JumpIndexDto { LibraryId = "", Buckets = [], Direction = direction };
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

            // The Volumes view (1.29.0) takes over a folder's name sort with volume cursors: no rail there. (Only the name sort: the
            // date sorts list the plain folder, as browse does.)
            if (!byDate)
            {
                var view = await _volumes.GetEntriesAsync(folderId, ct);
                if (view is { Available: true } && view.LibraryId == libraryId && await _volumes.IsActiveAsync(userId, view, group, ct))
                    return new JumpIndexDto { LibraryId = "", ParentId = parentPublicId, Buckets = [], Direction = direction };
            }
        }

        // The recently-read order cannot be walked by a cursor from here (an offset into an in-memory list): no rail.
        if (sort == "recentlyRead")
            return new JumpIndexDto { LibraryId = "", ParentId = parentPublicId, Buckets = [], Direction = direction };

        // The level browse lists: live children of the parent (or the root) ...
        IQueryable<CatalogNodeEntity> query = _db.CatalogNodes.AsNoTracking()
            .Where(n => n.LibraryId == libraryId)
            .Where(n => n.Availability != (int)CatalogNodeAvailability.Tombstoned);
        query = parentId is null ? query.Where(n => n.ParentId == null) : query.Where(n => n.ParentId == parentId);

        // ... under the same filters (1.49.0). Only an active filter pays for the pass.
        if (filter.IsActive)
            query = (await _browse.ApplyLevelFiltersAsync(query, userId, libraryId, parentId, filter, continuation: false, default, ct)).Query;

        // In the browse order of the sort: persisted SortKey (ordinal) for the name sort; folders first then newest first for
        // recentlyAdded; interleaved by effective recency (folders without a descendant last) for recentlyUpdated.
        const int folderKind = (int)CatalogNodeKind.Folder;
        var ordered = sort switch
        {
            JumpIndexBuilder.RecentlyAddedSort => query.OrderBy(n => n.Kind).ThenByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id),
            JumpIndexBuilder.RecentlyUpdatedSort => query
                .OrderByDescending(n => n.Kind == folderKind ? n.LatestDescendantAddedAt : (DateTimeOffset?)n.CreatedAt)
                .ThenBy(n => n.SortKey)
                .ThenBy(n => n.Id),
            _ => query.OrderBy(n => n.SortKey),
        };
        var rows = await ordered
            .Select(n => new { n.Id, n.PublicId, n.Kind, n.DisplayName, n.SortKey, n.CreatedAt, n.LatestDescendantAddedAt })
            .ToListAsync(ct);

        // Kept in the DATABASE order: the browse keyset (`SortKey > cursor` / `< cursor`) compares in SQL too, so the runs the
        // builder forms are exactly the runs the browse pages walk.
        var jumpRows = rows
            .Select(r => new JumpRow(r.PublicId, (CatalogNodeKind)r.Kind, r.DisplayName, r.SortKey, r.Id, r.CreatedAt, r.LatestDescendantAddedAt))
            .ToList();
        var offset = TimeSpan.FromMinutes(Math.Clamp(utcOffsetMinutes, -14 * 60, 14 * 60));
        var rail = byDate
            ? JumpIndexBuilder.BuildByDate(jumpRows, sort, _time.GetUtcNow(), offset)
            : JumpIndexBuilder.Build(jumpRows, descending, containerName);

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
                Group = b.Group,
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
