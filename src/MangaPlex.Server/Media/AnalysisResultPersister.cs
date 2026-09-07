namespace com.lifepixer.mangaplex.Server.Media;

using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Core.WorkerProtocol;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Persists a worker analysis <see cref="JobResult"/> into the catalog: page
/// entries plus the archive item's analysis state.
///
/// Shared by every path that receives an analyze result so a scanned library's
/// covers and manifests populate without each item being opened first. The
/// <see cref="MediaWorkerPool"/> calls this for every completed job (background
/// and reader-demand alike). Previously only the reader-demand path persisted,
/// so scan-enqueued background analysis analyzed-then-discarded its result.
///
/// Idempotent: existing pages are cleared and rewritten, so re-persisting the
/// same item is safe. Deterministic entry keys (ordinal-based) keep page URLs
/// stable across re-analyses (audit defect D4).
/// </summary>
public sealed class AnalysisResultPersister
{
    private readonly ILogger<AnalysisResultPersister>? _logger;

    public AnalysisResultPersister(ILogger<AnalysisResultPersister>? logger = null)
    {
        _logger = logger;
    }

    public async Task PersistAsync(MangaPlexDbContext db, long nodeId, JobResult result, CancellationToken ct = default)
    {
        var archiveItem = await db.ArchiveItems
            .Include(a => a.Pages)
            .FirstOrDefaultAsync(a => a.NodeId == nodeId, ct);
        if (archiveItem is null) return;

        if (!result.Success || result.Result is null)
        {
            archiveItem.AnalysisState = result.ErrorType switch
            {
                "encrypted" => 4,
                "unsupported" or "enumeration_error" => 3,
                "source_changed" or "source_missing" => 5,
                _ => 2,
            };
            archiveItem.AnalysisError = result.ErrorType;
            archiveItem.LastAnalyzedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        if (result.Result is not AnalyzeResult analyzeResult)
        {
            archiveItem.AnalysisState = 2;
            archiveItem.AnalysisError = "invalid_result";
            archiveItem.LastAnalyzedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        if (archiveItem.Pages.Count > 0)
            db.PageEntries.RemoveRange(archiveItem.Pages);

        foreach (var page in analyzeResult.Pages)
        {
            db.PageEntries.Add(new PageEntryEntity
            {
                ItemId = nodeId,
                ContentVersion = archiveItem.ContentVersion,
                Ordinal = page.Ordinal,
                EntryKey = new PageEntryKey(page.Ordinal).ToOpaque(),
                SourceEntryLocator = page.SourceEntryKey,
                MediaType = page.MediaType,
                Width = page.Width > 0 ? page.Width : null,
                Height = page.Height > 0 ? page.Height : null,
                AnimationState = (int)page.AnimationState,
                PageState = page.IsSupported ? 0 : 2,
                ByteSize = page.ByteSize,
            });
        }

        archiveItem.ArchiveFormat = (int)analyzeResult.ArchiveFormat;
        archiveItem.PageCount = analyzeResult.Pages.Count;
        archiveItem.AnalysisState = 0; // ready
        archiveItem.AnalysisError = null;
        archiveItem.LastAnalyzedAt = DateTimeOffset.UtcNow;
        archiveItem.ByteLength = analyzeResult.ObservedByteLength;
        archiveItem.ModificationTicks = analyzeResult.ObservedLastWriteTicks;

        await db.SaveChangesAsync(ct);
        _logger?.LogDebug("Analysis persisted for item {ItemId}: {PageCount} pages", nodeId, analyzeResult.Pages.Count);
    }
}
