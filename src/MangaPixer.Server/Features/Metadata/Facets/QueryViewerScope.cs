namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The libraries whose tags a viewer may see (D1 2.5 <c>@shown</c>): the libraries the viewer may list (admins all, readers their
/// grants), minus their Private libraries while Incognito is on, minus libraries with "Show series information" off - and nothing at
/// all while it is off for the whole instance (owner Q9). Sorted ascending; <see cref="Hash"/> names exactly this set (a cache key
/// part - two viewers share a cached count only when they see exactly the same libraries).
/// </summary>
public sealed record FacetViewerScope(IReadOnlyList<long> Shown, string Hash)
{
    public static readonly FacetViewerScope Empty = new([], HashOf([]));

    public bool IsEmpty => Shown.Count == 0;

    /// <summary>The set as a SQL list of integer literals (never user text).</summary>
    public string SqlList => string.Join(",", Shown.Select(id => id.ToString(CultureInfo.InvariantCulture)));

    public bool Contains(long libraryId) => Shown.Contains(libraryId);

    /// <summary>This scope narrowed to one library (the search's <c>libraryId</c>): empty when the library is not in it.</summary>
    public FacetViewerScope Narrow(long libraryId) => Contains(libraryId) ? Of([libraryId]) : Empty;

    /// <summary>A scope of exactly these libraries (sorted, distinct).</summary>
    public static FacetViewerScope Of(IEnumerable<long> libraryIds)
    {
        var sorted = libraryIds.Distinct().Order().ToList();
        return new FacetViewerScope(sorted, HashOf(sorted));
    }

    private static string HashOf(IReadOnlyList<long> sorted)
    {
        var text = string.Join(",", sorted.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16);
    }
}

/// <summary>Resolves a viewer's <see cref="FacetViewerScope"/> (scoped; two small queries).</summary>
public sealed class FacetViewerScopeResolver
{
    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _auth;

    public FacetViewerScopeResolver(MangaPixerDbContext db, LibraryAuthorizationService auth)
    {
        _db = db;
        _auth = auth;
    }

    public async Task<FacetViewerScope> ResolveAsync(long userId, bool incognito, CancellationToken ct = default)
    {
        var visible = await _auth.GetVisibleLibraryIdsAsync(userId, incognito, ct);
        if (visible.Count == 0)
            return FacetViewerScope.Empty;
        if (await IsHiddenEverywhereAsync(ct))
            return FacetViewerScope.Empty;

        var hidden = await _db.Libraries.AsNoTracking()
            .Where(l => l.MetadataSeriesInfoHidden)
            .Select(l => l.Id)
            .ToListAsync(ct);
        var hiddenSet = hidden.ToHashSet();
        return FacetViewerScope.Of(visible.Where(id => !hiddenSet.Contains(id)));
    }

    /// <summary>Whether "Show series information" is off for the whole instance.</summary>
    public Task<bool> IsHiddenEverywhereAsync(CancellationToken ct = default) =>
        _db.AppSettings.AsNoTracking().AnyAsync(s => s.Id == AppSettingsEntity.SingletonId && s.MetadataSeriesInfoHidden, ct);

    /// <summary>Whether a library shows no series information (its own switch or the instance's).</summary>
    public async Task<bool> IsHiddenAsync(long libraryId, CancellationToken ct = default) =>
        await IsHiddenEverywhereAsync(ct)
        || await _db.Libraries.AsNoTracking().AnyAsync(l => l.Id == libraryId && l.MetadataSeriesInfoHidden, ct);
}
