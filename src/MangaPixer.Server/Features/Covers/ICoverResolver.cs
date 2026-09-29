namespace com.lifepixer.mangapixer.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;

/// <summary>A node whose card needs a cover: its internal id, public id and whether it is a folder.</summary>
public readonly record struct CoverTarget(long NodeId, string PublicId, bool IsFolder);

/// <summary>A card's cover: its URL and where the image comes from (<see cref="CatalogNodeDto.CoverSource"/>).</summary>
public readonly record struct ResolvedCover(string Url, CardCoverSource Source);

/// <summary>
/// The ONE place a card's cover URL comes from (1.29.0 cover layer seam): browse, search, favourites, Home, Identify, the
/// Missing report and the review list all ask it instead of building <c>/api/v1/items/{id}/cover</c> themselves. The
/// implementation decides the layer (admin choice, automatic crop / web cover, file default); callers only render the URL.
/// The matcher's cover comparison does NOT use it - it compares FILE covers (<see cref="FolderCovers"/>), never the layer.
/// </summary>
public interface ICoverResolver
{
    /// <summary>
    /// Cover URL per <see cref="CoverTarget.NodeId"/>: an archive's own cover, a folder's resolved cover (its cover archive's,
    /// unless the layer says otherwise). Folders without a readable descendant archive and no layer cover are omitted.
    /// </summary>
    Task<IReadOnlyDictionary<long, string>> ResolveUrlsAsync(IReadOnlyCollection<CoverTarget> targets, CancellationToken ct);

    /// <summary>
    /// The same covers with their source (1.29.0 cover layer). The default reports every URL as a file cover, so a
    /// resolver that only knows URLs (the pass-through, test fakes) needs nothing more.
    /// </summary>
    async Task<IReadOnlyDictionary<long, ResolvedCover>> ResolveAsync(IReadOnlyCollection<CoverTarget> targets, CancellationToken ct) =>
        (await ResolveUrlsAsync(targets, ct)).ToDictionary(kv => kv.Key, kv => new ResolvedCover(kv.Value, CardCoverSource.File));
}

/// <summary>
/// The layered <see cref="ICoverResolver"/> (1.29.0 cover layer, the production registration): admin choice > "use the
/// file's cover" pin > automatic decision > file default, with versioned URLs (<see cref="CoverResolutionService"/>).
/// </summary>
public sealed class LayeredCoverResolver(CoverResolutionService resolutions) : ICoverResolver
{
    public async Task<IReadOnlyDictionary<long, string>> ResolveUrlsAsync(IReadOnlyCollection<CoverTarget> targets, CancellationToken ct) =>
        (await resolutions.ResolveAsync(targets, ct)).ToDictionary(kv => kv.Key, kv => kv.Value.Url);

    public async Task<IReadOnlyDictionary<long, ResolvedCover>> ResolveAsync(IReadOnlyCollection<CoverTarget> targets, CancellationToken ct) =>
        (await resolutions.ResolveAsync(targets, ct)).ToDictionary(kv => kv.Key, kv => new ResolvedCover(kv.Value.Url, kv.Value.Source));
}

/// <summary>
/// Pass-through <see cref="ICoverResolver"/> (1.29.0 contract): exactly the pre-1.29.0 covers - an archive's file cover, a
/// folder's first live descendant archive by SortKey (<see cref="FolderCovers"/>), unversioned. The fallback of services
/// built without DI (service tests); the container holds the <see cref="LayeredCoverResolver"/>.
/// </summary>
public sealed class FileCoverResolver(MangaPixerDbContext db) : ICoverResolver
{
    public async Task<IReadOnlyDictionary<long, string>> ResolveUrlsAsync(IReadOnlyCollection<CoverTarget> targets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var result = new Dictionary<long, string>(targets.Count);
        var folders = new List<long>();
        foreach (var t in targets)
        {
            if (t.IsFolder)
                folders.Add(t.NodeId);
            else
                result[t.NodeId] = FolderCovers.ArchiveCoverUrl(t.PublicId);
        }
        if (folders.Count > 0)
        {
            var covers = await FolderCovers.ResolveAsync(db, folders.Distinct().ToList(), ct);
            foreach (var (folderId, coverPublicId) in covers)
                result[folderId] = FolderCovers.ArchiveCoverUrl(coverPublicId);
        }
        return result;
    }
}
