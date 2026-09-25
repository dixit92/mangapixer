namespace com.lifepixer.mangapixer.Server.Media;

using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
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

    /// <param name="db">Scoped catalog context.</param>
    /// <param name="nodeId">Archive item / catalog node id.</param>
    /// <param name="result">Worker job result.</param>
    /// <param name="contentSignature">
    /// Optional <see cref="com.lifepixer.mangapixer.Core.Media.ContentSignature"/> of the
    /// analysed bytes (1.5.0). Stored on success so a later scan can recognise the
    /// file if it moves; ignored for failed results (the previous value, if any,
    /// is left untouched).
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public async Task PersistAsync(MangaPixerDbContext db, long nodeId, JobResult result, string? contentSignature = null, CancellationToken ct = default)
    {
        var archiveItem = await db.ArchiveItems
            .Include(a => a.Pages)
            .FirstOrDefaultAsync(a => a.NodeId == nodeId, ct);
        if (archiveItem is null)
        {
            _logger?.LogWarning(LogEvents.Worker.PersistSkippedMissingItem, "Persist skipped: archive item {ItemId} not found", nodeId);
            return;
        }

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
            _logger?.LogDebug(LogEvents.Worker.PersistFailureRecorded, "Persisted failure for item {ItemId}: state {State}, error {Error}",
                nodeId, archiveItem.AnalysisState, result.ErrorType);
            return;
        }

        if (result.Result is not AnalyzeResult analyzeResult)
        {
            archiveItem.AnalysisState = 2;
            archiveItem.AnalysisError = "invalid_result";
            archiveItem.LastAnalyzedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger?.LogWarning(LogEvents.Worker.PersistInvalidResult, "Persisted invalid result for item {ItemId}: result type {Type}", nodeId, result.Result.GetType().Name);
            return;
        }

        // Solid archives (all .cb7/.7z — SharpCompress reports every 7z as solid)
        // analyze successfully but can never be read page-by-page: HandleExtractAsync
        // refuses every page with "unsupported_solid". Marking the item ready (state 0)
        // here would leave its cover permanently broken with no error surfaced, since
        // solidity is only checked at extract time. Record it as unsupported instead so
        // the scan never leaves it silently stuck.
        if (analyzeResult.IsSolid)
        {
            archiveItem.AnalysisState = 3; // unsupported
            archiveItem.AnalysisError = "unsupported_solid";
            archiveItem.LastAnalyzedAt = DateTimeOffset.UtcNow;
            await StageComicInfoAsync(db, archiveItem, analyzeResult, ct);
            await db.SaveChangesAsync(ct);
            _logger?.LogDebug(LogEvents.Worker.PersistFailureRecorded, "Persisted unsupported_solid for item {ItemId}", nodeId);
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
        if (!string.IsNullOrEmpty(contentSignature))
            archiveItem.ContentSignature = contentSignature;

        // ComicInfo.xml (1.24.0) read by the worker while the archive was open:
        // stored in the same save as the pages, stamped with this content version.
        await StageComicInfoAsync(db, archiveItem, analyzeResult, ct);

        await db.SaveChangesAsync(ct);
        _logger?.LogDebug(LogEvents.Worker.PersistCompleted, "Analysis persisted for item {ItemId}: {PageCount} pages", nodeId, analyzeResult.Pages.Count);
    }

    /// <summary>
    /// Stages the ComicInfo outcome, when the worker sent one. A null outcome (an
    /// oversized result resent without it) leaves any existing row alone; the
    /// ComicInfo backfill picks the item up because its stamp is then stale.
    /// </summary>
    private static async Task StageComicInfoAsync(MangaPixerDbContext db, ArchiveItemEntity archiveItem, AnalyzeResult analyzeResult, CancellationToken ct)
    {
        if (analyzeResult.ComicInfo is null)
            return;
        await ComicInfoPersister.StageAsync(db, archiveItem.NodeId, archiveItem.ContentVersion, analyzeResult.ComicInfo, DateTimeOffset.UtcNow, ct);
    }
}
