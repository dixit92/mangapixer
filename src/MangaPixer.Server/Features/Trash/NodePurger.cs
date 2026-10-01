namespace com.lifepixer.mangapixer.Server.Features.Trash;

using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// What removing catalog nodes needs beyond the rows their foreign keys cascade to (1.31.0, extracted from library delete): the
/// per-user rows keyed by item id with no foreign key (reading progress, read marks, bookmarks, reader overrides), and the
/// node's own files in the data root (its thumbnail and its cover crops). Library delete and the trash both use it. Web covers,
/// posters and metadata records belong to their record and are never touched here.
/// </summary>
public sealed class NodePurger
{
    private readonly MangaPixerDbContext _db;
    private readonly ThumbnailStore? _thumbnails;
    private readonly CoverFiles? _coverFiles;

    public NodePurger(MangaPixerDbContext db, ThumbnailStore? thumbnails = null, CoverFiles? coverFiles = null)
    {
        _db = db;
        _thumbnails = thumbnails;
        _coverFiles = coverFiles;
    }

    /// <summary>
    /// The data-root files of these archives: the thumbnail of each archive's current thumbnail content version, and the
    /// archives whose cover crops go. Read BEFORE the rows are removed (the content versions live on them).
    /// </summary>
    public async Task<NodeFileKeys> CollectFilesAsync(IReadOnlyCollection<long> archiveIds, CancellationToken ct)
    {
        if (archiveIds.Count == 0)
            return NodeFileKeys.Empty;
        var thumbnails = await _db.ArchiveItems.AsNoTracking()
            .Where(a => archiveIds.Contains(a.NodeId) && a.ThumbnailContentVersion != null)
            .Select(a => new ThumbnailKey(a.NodeId, a.ThumbnailContentVersion!.Value))
            .ToListAsync(ct);
        return new NodeFileKeys(thumbnails, archiveIds.ToList());
    }

    /// <summary>How many of these files exist and their size (the trash preview). File system only.</summary>
    public FileTotals MeasureFiles(NodeFileKeys keys)
    {
        var totals = new FileTotals(0, 0);
        foreach (var path in ExistingFiles(keys))
            totals = totals.Add(SizeOf(path));
        return totals;
    }

    /// <summary>Deletes these files (best effort; a file that cannot be deleted is left for Clean bundles). File system only - call it outside a write transaction.</summary>
    public FileTotals DeleteFiles(NodeFileKeys keys)
    {
        var totals = new FileTotals(0, 0);
        foreach (var path in ExistingFiles(keys))
        {
            var size = SizeOf(path);
            if (TryDelete(path))
                totals = totals.Add(size);
        }
        return totals;
    }

    /// <summary>Counts the per-user rows of these items (reading progress, read marks, bookmarks, reader overrides) and favorites of these nodes.</summary>
    public async Task<UserStateCounts> CountUserStateAsync(IReadOnlyCollection<long> nodeIds, CancellationToken ct)
    {
        if (nodeIds.Count == 0)
            return UserStateCounts.None;
        return new UserStateCounts(
            await _db.ReadingProgress.CountAsync(p => nodeIds.Contains(p.ItemId), ct),
            await _db.ReadMarks.CountAsync(m => nodeIds.Contains(m.ItemId), ct),
            await _db.Bookmarks.CountAsync(b => nodeIds.Contains(b.ItemId), ct),
            await _db.ItemReaderOverrides.CountAsync(o => nodeIds.Contains(o.ItemId), ct),
            await _db.Favorites.CountAsync(f => nodeIds.Contains(f.CatalogNodeId), ct));
    }

    /// <summary>
    /// Deletes the per-user rows keyed by item id that have no foreign key to the catalog (reading progress, read marks,
    /// bookmarks, reader overrides). Favorites are not counted here: they cascade with the node. Runs in the caller's transaction.
    /// </summary>
    public async Task<UserStateCounts> DeleteUserStateAsync(IReadOnlyCollection<long> itemIds, CancellationToken ct)
    {
        if (itemIds.Count == 0)
            return UserStateCounts.None;
        return new UserStateCounts(
            await _db.ReadingProgress.Where(p => itemIds.Contains(p.ItemId)).ExecuteDeleteAsync(ct),
            await _db.ReadMarks.Where(m => itemIds.Contains(m.ItemId)).ExecuteDeleteAsync(ct),
            await _db.Bookmarks.Where(b => itemIds.Contains(b.ItemId)).ExecuteDeleteAsync(ct),
            await _db.ItemReaderOverrides.Where(o => itemIds.Contains(o.ItemId)).ExecuteDeleteAsync(ct),
            0);
    }

    private IEnumerable<string> ExistingFiles(NodeFileKeys keys)
    {
        if (_thumbnails is not null)
        {
            foreach (var key in keys.Thumbnails)
            {
                var path = _thumbnails.GetThumbnailPath(key.ItemId, key.ContentVersion);
                if (File.Exists(path))
                    yield return path;
            }
        }
        if (_coverFiles is not null)
        {
            foreach (var archiveId in keys.CropArchives)
            {
                foreach (var path in _coverFiles.CropFilesOf(archiveId))
                    yield return path;
            }
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>A thumbnail file key: the item and the content version the thumbnail was made from.</summary>
public readonly record struct ThumbnailKey(long ItemId, long ContentVersion);

/// <summary>The data-root files of a set of archives (see <see cref="NodePurger.CollectFilesAsync"/>).</summary>
public sealed record NodeFileKeys(IReadOnlyList<ThumbnailKey> Thumbnails, IReadOnlyList<long> CropArchives)
{
    public static NodeFileKeys Empty { get; } = new([], []);
}

/// <summary>A number of files and their size in bytes.</summary>
public readonly record struct FileTotals(int Files, long Bytes)
{
    public FileTotals Add(long bytes) => new(Files + 1, Bytes + bytes);

    public FileTotals Add(FileTotals other) => new(Files + other.Files, Bytes + other.Bytes);
}

/// <summary>Per-user rows of a set of nodes.</summary>
public readonly record struct UserStateCounts(int Progress, int ReadMarks, int Bookmarks, int ReaderOverrides, int Favorites)
{
    public static UserStateCounts None { get; } = new(0, 0, 0, 0, 0);

    public int Total => Progress + ReadMarks + Bookmarks + ReaderOverrides + Favorites;
}
