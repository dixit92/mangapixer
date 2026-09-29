namespace com.lifepixer.mangapixer.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>A rendered crop: its file, size and perceptual hash (null when the file already existed and was not re-rendered).</summary>
public sealed record CoverCrop(string Path, ulong? Hash, int SourceWidth, int SourceHeight);

/// <summary>
/// Renders one half of an archive's page 1 (a jacket spread) into the data root's crop store through the media worker
/// (protocol 5 <c>cover_render</c>; the server never opens the archive or decodes the image). The worker's output path is
/// always a temp file NEXT TO the crop's final place in <see cref="CoverFiles.CropsRoot"/>; the archive locator is the
/// same validated library root + relative path + page entry locator the thumbnail uses. No network.
/// </summary>
public sealed class CoverCropService
{
    private readonly MangaPixerDbContext _db;
    private readonly ICoverRenderer _renderer;
    private readonly CoverFiles _files;
    private readonly ILogger<CoverCropService>? _logger;

    public CoverCropService(MangaPixerDbContext db, ICoverRenderer renderer, CoverFiles files, ILogger<CoverCropService>? logger = null)
    {
        _db = db;
        _renderer = renderer;
        _files = files;
        _logger = logger;
    }

    /// <summary>
    /// The crop of <paramref name="side"/> of the archive's page 1 at its current content version: the existing file, or a
    /// fresh render (with its hash) when <paramref name="rerender"/> or it does not exist. Null when the archive is not
    /// ready or the worker refused (a code is logged).
    /// </summary>
    public async Task<CoverCrop?> EnsureAsync(long archiveNodeId, CoverCropSide side, bool rerender, CancellationToken ct)
    {
        var item = await _db.ArchiveItems.AsNoTracking().FirstOrDefaultAsync(a => a.NodeId == archiveNodeId, ct);
        if (item is null || item.AnalysisState != 0)
            return null;
        var destination = _files.CropPath(archiveNodeId, item.ContentVersion, side);
        if (!rerender && File.Exists(destination))
            return new CoverCrop(destination, null, 0, 0);

        var firstPage = await _db.PageEntries.AsNoTracking()
            .Where(p => p.ItemId == archiveNodeId && p.ContentVersion == item.ContentVersion)
            .OrderBy(p => p.Ordinal)
            .Select(p => p.SourceEntryLocator)
            .FirstOrDefaultAsync(ct);
        if (firstPage is null)
            return null;
        var source = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.Id == archiveNodeId)
            .Join(_db.Libraries, n => n.LibraryId, l => l.Id, (n, l) => new { l.RootPath, n.RelativePath })
            .FirstOrDefaultAsync(ct);
        if (source is null)
            return null;
        var archivePath = Path.Combine(source.RootPath, source.RelativePath);
        if (!File.Exists(archivePath))
            return null;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            var outcome = await _renderer.RenderAsync(new CoverRenderRequest
            {
                JobId = "crop",
                Source = CoverRenderSources.Archive,
                ArchivePath = archivePath,
                SourceEntryKey = firstPage,
                ExpectedLastWriteTicks = item.ModificationTicks,
                ExpectedByteLength = item.ByteLength,
                CropSide = side == CoverCropSide.Left ? CoverCropSides.Left : CoverCropSides.Right,
                OutputPath = temp,
                MaxDimension = CoverRenderLimits.DefaultMaxDimension,
                WebpQuality = CoverRenderLimits.DefaultWebpQuality,
                ComputeHash = true,
            }, ct);
            if (!outcome.Success || !File.Exists(temp))
            {
                _logger?.LogDebug(LogEvents.Metadata.CoverCropFailed, "Cover crop failed (item {ItemId}): {Error}", archiveNodeId, outcome.ErrorType);
                return null;
            }
            CoverFiles.Publish(temp, destination);
            _files.DeleteCrops(archiveNodeId, keepContentVersion: item.ContentVersion);
            _logger?.LogDebug(LogEvents.Metadata.CoverCropRendered, "Cover crop rendered (item {ItemId}, side {Side})", archiveNodeId, side);
            return new CoverCrop(destination, outcome.Hash, outcome.SourceWidth, outcome.SourceHeight);
        }
        finally
        {
            CoverFiles.TryDelete(temp);
        }
    }
}
