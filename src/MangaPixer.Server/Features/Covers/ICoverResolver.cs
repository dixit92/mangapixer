namespace com.lifepixer.mangapixer.Server.Features.Covers;

using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;

/// <summary>A node whose card needs a cover: its internal id, public id and whether it is a folder.</summary>
public readonly record struct CoverTarget(long NodeId, string PublicId, bool IsFolder);

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
}

/// <summary>
/// Pass-through <see cref="ICoverResolver"/> (1.29.0 contract): exactly the pre-1.29.0 covers - an archive's file cover, a
/// folder's first live descendant archive by SortKey (<see cref="FolderCovers"/>). Lane C replaces it with the layered resolver.
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
