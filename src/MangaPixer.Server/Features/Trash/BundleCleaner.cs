namespace com.lifepixer.mangapixer.Server.Features.Trash;

using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// "Clean bundles" (1.31.0): files in the data root that no row references any more. Each store lists only the files whose
/// names it writes (an unknown file is never touched); a file is unreferenced when no row names its key:
/// <list type="bullet">
/// <item>a thumbnail <c>&lt;id&gt;-&lt;cv&gt;</c> - no archive item with that id whose thumbnail or current content version is cv;</item>
/// <item>a cover crop <c>&lt;id&gt;-&lt;cv&gt;-&lt;side&gt;</c> - no archive item with that id at content version cv;</item>
/// <item>a web cover <c>&lt;publicId&gt;-&lt;version&gt;</c> - no volume cover row with that public id and stored version;</item>
/// <item>a series image <c>&lt;recordId&gt;-&lt;version&gt;</c> - no metadata record with that id and image version;</item>
/// <item>a leftover temp file of the thumbnail or series-image store.</item>
/// </list>
/// Files younger than <see cref="MinimumAge"/> are skipped: a write may be in flight whose row is not committed yet.
/// </summary>
public sealed class BundleCleaner
{
    /// <summary>Files younger than this are never cleaned (in-flight writes).</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    private readonly MangaPixerDbContext _db;
    private readonly TimeProvider _time;
    private readonly ThumbnailStore? _thumbnails;
    private readonly CoverFiles? _coverFiles;
    private readonly VolumeCoverStore? _volumeCovers;
    private readonly MetadataImageStore? _images;

    public BundleCleaner(
        MangaPixerDbContext db,
        TimeProvider? time = null,
        ThumbnailStore? thumbnails = null,
        CoverFiles? coverFiles = null,
        VolumeCoverStore? volumeCovers = null,
        MetadataImageStore? images = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
        _thumbnails = thumbnails;
        _coverFiles = coverFiles;
        _volumeCovers = volumeCovers;
        _images = images;
    }

    /// <summary>How many unreferenced files there are and their size.</summary>
    public async Task<FileTotals> PreviewAsync(CancellationToken ct)
    {
        var totals = new FileTotals(0, 0);
        foreach (var file in await FindAsync(ct))
            totals = totals.Add(file.Length);
        return totals;
    }

    /// <summary>Deletes the unreferenced files (best effort); returns what was removed.</summary>
    public async Task<FileTotals> CleanAsync(CancellationToken ct)
    {
        var totals = new FileTotals(0, 0);
        foreach (var file in await FindAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (NodePurger.TryDelete(file.FullName))
                totals = totals.Add(file.Length);
        }
        return totals;
    }

    /// <summary>The unreferenced files old enough to clean. Reads the rows first, then lists the files.</summary>
    public async Task<IReadOnlyList<FileInfo>> FindAsync(CancellationToken ct)
    {
        var found = new List<FileInfo>();
        var cutoff = (_time.GetUtcNow() - MinimumAge).UtcDateTime;

        if (_thumbnails is not null || _coverFiles is not null)
        {
            var items = await _db.ArchiveItems.AsNoTracking()
                .Select(a => new { a.NodeId, a.ContentVersion, a.ThumbnailContentVersion })
                .ToListAsync(ct);
            var current = items.Select(a => (a.NodeId, a.ContentVersion)).ToHashSet();
            if (_thumbnails is not null)
            {
                var thumbnails = new HashSet<(long, long)>(current);
                thumbnails.UnionWith(items.Where(a => a.ThumbnailContentVersion != null).Select(a => (a.NodeId, a.ThumbnailContentVersion!.Value)));
                AddOld(found, _thumbnails.EnumerateThumbnails().Where(t => !thumbnails.Contains((t.ItemId, t.ContentVersion))).Select(t => t.Path), cutoff);
                AddOld(found, _thumbnails.EnumerateTempFiles(), cutoff);
            }
            if (_coverFiles is not null)
                AddOld(found, _coverFiles.EnumerateCrops().Where(c => !current.Contains((c.ArchiveNodeId, c.ContentVersion))).Select(c => c.Path), cutoff);
        }

        if (_volumeCovers is not null)
        {
            var kept = (await _db.VolumeCovers.AsNoTracking()
                    .Where(c => c.StoredVersion > 0)
                    .Select(c => new { c.PublicId, c.StoredVersion })
                    .ToListAsync(ct))
                .Select(c => (c.PublicId, c.StoredVersion))
                .ToHashSet();
            AddOld(found, _volumeCovers.EnumerateStored().Where(c => !kept.Contains((c.PublicId, c.Version))).Select(c => c.Path), cutoff);
        }

        if (_images is not null)
        {
            var kept = (await _db.MetadataRecords.AsNoTracking()
                    .Select(r => new { r.Id, r.ImageVersion })
                    .ToListAsync(ct))
                .Select(r => (r.Id, r.ImageVersion))
                .ToHashSet();
            AddOld(found, _images.EnumerateStored().Where(i => !kept.Contains((i.RecordId, i.Version))).Select(i => i.Path), cutoff);
            AddOld(found, _images.EnumerateTempFiles(), cutoff);
        }

        return found;
    }

    private static void AddOld(List<FileInfo> found, IEnumerable<string> paths, DateTime cutoffUtc)
    {
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.LastWriteTimeUtc < cutoffUtc)
                    found.Add(info);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
