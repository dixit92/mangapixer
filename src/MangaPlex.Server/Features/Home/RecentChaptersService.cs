namespace com.lifepixer.mangaplex.Server.Features.Home;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Home "New chapters" service (1.11.0 Lane C). Returns the most-recently-added
/// archives for each library the caller can see, grouped by library, newest
/// first, capped per library.
///
/// Query design (independent of <c>CatalogBrowseService</c>, which a parallel
/// lane owns):
/// <list type="bullet">
/// <item><description>
/// Visibility uses <see cref="LibraryAuthorizationService.GetVisibleLibraryIdsAsync"/>
/// (accessible minus the caller's Private set while Incognito is active) - the
/// same server-side chokepoint browse/home/search use, so a Private library's
/// items never leak.
/// </description></item>
/// <item><description>
/// Recency is newest-first by <c>CreatedAt DESC, Id DESC</c> (Id is the stable
/// tiebreaker), matching the browse <c>recentlyAdded</c> sort's per-archive
/// ordering. Only archive nodes (Kind == 1) are considered; tombstoned nodes
/// (Availability == 5) are excluded, matching browse's base filter.
/// </description></item>
/// <item><description>
/// The cap is per library. Libraries are few (the home page renders a library
/// grid), so a per-library <c>OrderByDescending(CreatedAt).Take(perLibrary)</c>
/// query is an indexed ordered scan bounded to the cap - cheaper and simpler
/// than a correlated subquery window over a large library, and avoids an N+1
/// over archives.
/// </description></item>
/// <item><description>
/// Each entry carries its immediate parent folder's public id and display name
/// so the frontend can label/group by series where natural. No source paths.
/// </description></item>
/// </list>
/// </summary>
public sealed class RecentChaptersService
{
    /// <summary>Default per-library cap (matches the home continue-reading limit).</summary>
    public const int DefaultPerLibrary = 12;

    /// <summary>Maximum per-library cap accepted from a caller.</summary>
    public const int MaxPerLibrary = 50;

    private readonly MangaPlexDbContext _db;
    private readonly LibraryAuthorizationService _auth;

    public RecentChaptersService(MangaPlexDbContext db, LibraryAuthorizationService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <summary>
    /// Returns the most-recently-added archives grouped by visible library,
    /// newest first, capped per library. Empty <see cref="RecentChaptersDto.Libraries"/>
    /// when the caller can see no libraries; a library with no archives appears
    /// with an empty items list.
    /// </summary>
    public async Task<RecentChaptersDto> GetRecentChaptersAsync(
        long userId,
        int? perLibrary = null,
        bool incognito = false,
        CancellationToken ct = default)
    {
        var cap = perLibrary is null or < 1
            ? DefaultPerLibrary
            : Math.Min(perLibrary.Value, MaxPerLibrary);

        var visibleLibs = await _auth.GetVisibleLibraryIdsAsync(userId, incognito, ct);
        if (visibleLibs.Count == 0)
            return new RecentChaptersDto { Libraries = [] };

        // Visible libraries ordered by display name for a stable, predictable
        // home row order (matches the library list endpoint's presentation).
        var libs = await _db.Libraries
            .Where(l => visibleLibs.Contains(l.Id))
            .OrderBy(l => l.DisplayName)
            .Select(l => new { l.Id, l.PublicId, l.DisplayName })
            .ToListAsync(ct);

        var groups = new List<RecentChaptersLibraryGroup>(libs.Count);
        foreach (var lib in libs)
        {
            var items = await _db.CatalogNodes
                .Where(n => n.LibraryId == lib.Id
                    && n.Kind == (int)CatalogNodeKind.Archive
                    && n.Availability != (int)CatalogNodeAvailability.Tombstoned)
                .OrderByDescending(n => n.CreatedAt)
                .ThenByDescending(n => n.Id)
                .Take(cap)
                .Select(n => new RecentChapterEntry
                {
                    ItemId = n.PublicId,
                    DisplayName = n.DisplayName,
                    LibraryId = lib.PublicId,
                    ParentId = n.Parent != null ? n.Parent.PublicId : "",
                    SeriesName = n.Parent != null ? n.Parent.DisplayName : null,
                    AddedAt = n.CreatedAt,
                    PageCount = n.ArchiveItem != null ? n.ArchiveItem.PageCount : null,
                })
                .ToListAsync(ct);

            groups.Add(new RecentChaptersLibraryGroup
            {
                LibraryId = lib.PublicId,
                LibraryName = lib.DisplayName,
                Items = items,
            });
        }

        return new RecentChaptersDto { Libraries = groups };
    }
}
