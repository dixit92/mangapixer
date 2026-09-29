namespace com.lifepixer.mangapixer.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Which way a book reads, for which half of a jacket spread is its front (1.29.0 cover layer). User independent - the
/// cover is the same for everyone, so the per-user steps of <c>ReaderModeResolver</c> are skipped. First that applies:
/// <list type="number">
/// <item>the nearest folder reader default (<c>folder_reader_defaults</c>), then the library default - the admin's
/// <c>ReaderModeResolver</c> steps 2-3: <see cref="ReaderMode.PagedRtl"/> = right-to-left, <see cref="ReaderMode.PagedLtr"/> =
/// left-to-right (other modes say nothing about direction);</item>
/// <item>the archive's ComicInfo <c>Manga</c>: <c>Yes</c> / <c>YesAndRightToLeft</c> = right-to-left, <c>No</c> = left-to-right;</item>
/// <item>the linked record's origin: Japan = right-to-left; any other known origin = left-to-right;</item>
/// <item>left-to-right.</item>
/// </list>
/// </summary>
public sealed class CoverDirectionResolver(MangaPixerDbContext db)
{
    public const int MaxWalkDepth = 64;

    public async Task<CoverDirection> ResolveAsync(long archiveNodeId, MetadataOrigin? linkedOrigin, CancellationToken ct)
    {
        var node = await db.CatalogNodes.AsNoTracking().Where(n => n.Id == archiveNodeId)
            .Select(n => new { n.ParentId, n.LibraryId }).FirstOrDefaultAsync(ct);
        if (node is null)
            return CoverDirection.LeftToRight;

        // 1. The admin's reader defaults: nearest ancestor folder, then the library.
        var ancestors = new List<long>();
        var parentId = node.ParentId;
        var guard = 0;
        while (parentId is { } pid && guard++ < MaxWalkDepth)
        {
            ancestors.Add(pid);
            parentId = await db.CatalogNodes.Where(n => n.Id == pid).Select(n => n.ParentId).FirstOrDefaultAsync(ct);
        }
        if (ancestors.Count > 0)
        {
            var defaults = await db.FolderReaderDefaults.AsNoTracking().Where(f => ancestors.Contains(f.NodeId))
                .ToDictionaryAsync(f => f.NodeId, f => f.ReaderMode, ct);
            foreach (var id in ancestors)
                if (defaults.TryGetValue(id, out var mode) && FromReaderMode(mode) is { } folderDirection)
                    return folderDirection;
        }
        var libraryMode = await db.Libraries.AsNoTracking().Where(l => l.Id == node.LibraryId).Select(l => l.DefaultReaderMode).FirstOrDefaultAsync(ct);
        if (libraryMode is { } lm && FromReaderMode(lm) is { } libraryDirection)
            return libraryDirection;

        // 2. ComicInfo Manga.
        var manga = await db.EmbeddedMetadata.AsNoTracking().Where(e => e.NodeId == archiveNodeId).Select(e => e.MangaDirection).FirstOrDefaultAsync(ct);
        if (FromComicInfo(manga) is { } comicDirection)
            return comicDirection;

        // 3. The linked record's origin; 4. left-to-right.
        return FromOrigin(linkedOrigin);
    }

    /// <summary>A reader mode's direction: PagedRtl / PagedLtr; null for the modes that do not say.</summary>
    public static CoverDirection? FromReaderMode(int mode) => (ReaderMode)mode switch
    {
        ReaderMode.PagedRtl => CoverDirection.RightToLeft,
        ReaderMode.PagedLtr => CoverDirection.LeftToRight,
        _ => null,
    };

    /// <summary>ComicInfo <c>Manga</c> (0 No, 1 Yes, 2 YesAndRightToLeft); null when absent.</summary>
    public static CoverDirection? FromComicInfo(int? manga) => manga switch
    {
        1 or 2 => CoverDirection.RightToLeft,
        0 => CoverDirection.LeftToRight,
        _ => null,
    };

    public static CoverDirection FromOrigin(MetadataOrigin? origin) =>
        origin == MetadataOrigin.Japan ? CoverDirection.RightToLeft : CoverDirection.LeftToRight;
}
