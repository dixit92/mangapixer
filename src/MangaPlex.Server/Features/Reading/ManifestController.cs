namespace com.lifepixer.mangaplex.Server.Features.Reading;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Core.Media;
using com.lifepixer.mangaplex.Core.Reading;
using com.lifepixer.mangaplex.Core.WorkerProtocol;
using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Media;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Manifest and readiness endpoints for readable items.
/// GET /api/v1/items/{itemId}/manifest — page manifest (after analysis).
/// GET /api/v1/items/{itemId}/readiness — analysis state.
/// POST /api/v1/items/{itemId}/prepare — trigger analysis if not yet done.
/// </summary>
[ApiController]
[Route("api/v1/items")]
[Authorize]
public sealed class ManifestController : ControllerBase
{
    private readonly MangaPlexDbContext _db;
    private readonly MediaWorkerPool _workerPool;
    private readonly JobScheduler _jobScheduler;
    private readonly LibraryAuthorizationService _libraryAuth;
    private readonly ILogger<ManifestController> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public ManifestController(
        MangaPlexDbContext db,
        MediaWorkerPool workerPool,
        JobScheduler jobScheduler,
        LibraryAuthorizationService libraryAuth,
        ILogger<ManifestController> logger,
        IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _workerPool = workerPool;
        _jobScheduler = jobScheduler;
        _libraryAuth = libraryAuth;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    [HttpGet("{itemId}/manifest")]
    public async Task<IActionResult> GetManifest(string itemId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var node = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.PublicId == itemId, ct);
        if (node is null) return NotFound();

        // Check library access
        if (!await HasAccessAsync(userId.Value, node.LibraryId, ct))
            return NotFound();

        if (node.Kind != 1) // not an archive
            return BadRequest(new ApiError { Error = "not_readable", Message = "Item is not a readable archive." });

        var archiveItem = await _db.ArchiveItems
            .Include(a => a.Pages)
            .FirstOrDefaultAsync(a => a.NodeId == node.Id, ct);

        // Audit defect D31: pending item → enqueue analysis and return 202
        // with ItemReadiness, not 404.
        if (archiveItem is null || archiveItem.AnalysisState != 0)
        {
            // Enqueue analysis at CurrentPage priority (reader is waiting)
            await EnqueueAnalysisAsync(node, archiveItem, ct);

            var state = archiveItem?.AnalysisState switch
            {
                1 => ItemReadinessState.Pending,
                2 => ItemReadinessState.Failed,
                3 => ItemReadinessState.Unsupported,
                4 => ItemReadinessState.Encrypted,
                5 => ItemReadinessState.Missing,
                _ => ItemReadinessState.Pending,
            };

            return StatusCode(202, new ItemReadiness
            {
                ItemId = itemId,
                State = state,
                ContentVersion = archiveItem?.ContentVersion ?? 0,
                Error = archiveItem?.AnalysisError,
                LastAttempt = archiveItem?.LastAnalyzedAt,
                IsAnalyzing = true,
            });
        }

        var manifest = BuildManifest(itemId, archiveItem);
        return Ok(manifest);
    }

    [HttpGet("{itemId}/readiness")]
    public async Task<IActionResult> GetReadiness(string itemId, CancellationToken ct)
    {
        return await GetReadinessInternal(itemId, ct);
    }

    /// <summary>
    /// Alias for /readiness (audit defect D31 — the frozen contract says
    /// /preparation; keep /readiness for one release as an alias).
    /// </summary>
    [HttpGet("{itemId}/preparation")]
    public async Task<IActionResult> GetPreparation(string itemId, CancellationToken ct)
    {
        return await GetReadinessInternal(itemId, ct);
    }

    private async Task<IActionResult> GetReadinessInternal(string itemId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var node = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.PublicId == itemId, ct);
        if (node is null) return NotFound();

        if (!await HasAccessAsync(userId.Value, node.LibraryId, ct))
            return NotFound();

        var archiveItem = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == node.Id, ct);

        var state = archiveItem?.AnalysisState switch
        {
            0 => ItemReadinessState.Ready,
            1 => ItemReadinessState.Pending,
            2 => ItemReadinessState.Failed,
            3 => ItemReadinessState.Unsupported,
            4 => ItemReadinessState.Encrypted,
            5 => ItemReadinessState.Missing,
            _ => ItemReadinessState.Pending,
        };

        // Check if there's an active job
        var isAnalyzing = await _db.Jobs
            .AnyAsync(j => j.ItemId == node.Id && j.Status == 1, ct);

        return Ok(new ItemReadiness
        {
            ItemId = itemId,
            State = state,
            ContentVersion = archiveItem?.ContentVersion ?? 0,
            Error = archiveItem?.AnalysisError,
            LastAttempt = archiveItem?.LastAnalyzedAt,
            IsAnalyzing = isAnalyzing,
        });
    }

    [HttpPost("{itemId}/prepare")]
    public async Task<IActionResult> Prepare(string itemId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var node = await _db.CatalogNodes
            .Include(n => n.Library)
            .FirstOrDefaultAsync(n => n.PublicId == itemId, ct);
        if (node is null) return NotFound();

        if (!await HasAccessAsync(userId.Value, node.LibraryId, ct))
            return NotFound();

        if (node.Kind != 1)
            return BadRequest(new ApiError { Error = "not_readable", Message = "Item is not a readable archive." });

        var archiveItem = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == node.Id, ct);

        // Already analyzed and ready
        if (archiveItem is not null && archiveItem.AnalysisState == 0 && archiveItem.PageCount > 0)
            return Ok(new { status = "ready", contentVersion = archiveItem.ContentVersion });

        await EnqueueAnalysisAsync(node, archiveItem, ct);

        return Accepted(new { status = "analyzing", contentVersion = archiveItem?.ContentVersion ?? 0 });
    }

    /// <summary>
    /// Enqueues an analysis job for the node and marks it as pending.
    /// Source path computation stays here, not in the controller route handler
    /// (audit defect D31 — move source-path computation out of the controller).
    /// </summary>
    private async Task EnqueueAnalysisAsync(
        CatalogNodeEntity node,
        ArchiveItemEntity? archiveItem,
        CancellationToken ct)
    {
        // Get source path from the library root + relative path
        await _db.Entry(node).Reference(n => n.Library).LoadAsync(ct);
        var sourcePath = Path.Combine(node.Library!.RootPath, node.RelativePath);
        var fileInfo = new FileInfo(sourcePath);
        if (!fileInfo.Exists)
            return;

        var contentVersion = archiveItem?.ContentVersion ?? 0;
        var lastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks;
        var byteLength = fileInfo.Length;

        // Enqueue analysis job at CurrentPage priority (reader is waiting)
        var jobTask = _jobScheduler.EnqueueAsync(
            itemId: node.Id,
            contentVersion: contentVersion,
            operation: JobOperation.Analyze,
            priority: JobPriority.CurrentPage,
            archivePath: sourcePath,
            expectedLastWriteTicks: lastWriteTicks,
            expectedByteLength: byteLength,
            callerToken: CancellationToken.None); // don't cancel on request end

        // Mark as pending in DB
        if (archiveItem is null)
        {
            archiveItem = new ArchiveItemEntity
            {
                NodeId = node.Id,
                ContentVersion = contentVersion,
                AnalysisState = 1, // pending
                ByteLength = byteLength,
                ModificationTicks = lastWriteTicks,
            };
            _db.ArchiveItems.Add(archiveItem);
        }
        else
        {
            archiveItem.AnalysisState = 1;
        }
        await _db.SaveChangesAsync(ct);

        // Dispatch to worker pool (fire-and-forget, but process the result)
        var nodeIdForTask = node.Id;
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            try
            {
                await _workerPool.DispatchAsync(CancellationToken.None);
                var result = await jobTask;
                await PersistAnalysisResultAsync(scopedDb, nodeIdForTask, result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Prepare failed for item {ItemId}: {Error}", nodeIdForTask, ex.GetType().Name);
                await MarkAnalysisFailedAsync(scopedDb, nodeIdForTask, ex.GetType().Name);
            }
        }, CancellationToken.None);
    }

    private async Task PersistAnalysisResultAsync(MangaPlexDbContext db, long nodeId, JobResult result)
    {
        var archiveItem = await db.ArchiveItems
            .Include(a => a.Pages)
            .FirstOrDefaultAsync(a => a.NodeId == nodeId);
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
            await db.SaveChangesAsync();
            return;
        }

        var analyzeResult = (result.Result as AnalyzeResult);
        if (analyzeResult is null)
        {
            archiveItem.AnalysisState = 2;
            archiveItem.AnalysisError = "invalid_result";
            archiveItem.LastAnalyzedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return;
        }

        // Clear old pages
        if (archiveItem.Pages.Count > 0)
        {
            db.PageEntries.RemoveRange(archiveItem.Pages);
        }

        // Persist new pages — entry keys are deterministic ordinals, not
        // random strings, so page URLs are stable across re-analyses
        // (audit defect D4).
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

        await db.SaveChangesAsync();
        _logger.LogInformation("Analysis persisted for item {ItemId}: {PageCount} pages",
            nodeId, analyzeResult.Pages.Count);
    }

    private async Task MarkAnalysisFailedAsync(MangaPlexDbContext db, long nodeId, string errorType)
    {
        try
        {
            var archiveItem = await db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == nodeId);
            if (archiveItem is not null)
            {
                archiveItem.AnalysisState = 2;
                archiveItem.AnalysisError = errorType;
                archiveItem.LastAnalyzedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch { /* best effort */ }
    }

    private static ItemManifest BuildManifest(string itemId, ArchiveItemEntity archiveItem)
    {
        var pages = archiveItem.Pages
            .OrderBy(p => p.Ordinal)
            .Select(p => new ManifestPageEntry
            {
                EntryKey = p.EntryKey,
                PageIndex = p.Ordinal,
                MediaType = p.MediaType,
                Width = p.Width ?? 0,
                Height = p.Height ?? 0,
                AnimationState = (AnimationState)p.AnimationState,
                ByteSize = p.ByteSize,
            })
            .ToList();

        return new ItemManifest
        {
            ItemId = itemId,
            ContentVersion = archiveItem.ContentVersion,
            ManifestVersion = 1,
            ArchiveFormat = (ArchiveFormat)archiveItem.ArchiveFormat,
            PageCount = archiveItem.PageCount ?? 0,
            Pages = pages,
            IsSolid = false,
            HasAnimatedPages = pages.Any(p => p.AnimationState == AnimationState.Animated),
        };
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var id))
            return null;
        return id;
    }

    private async Task<bool> HasAccessAsync(long userId, long libraryId, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return false;
        if (user.IsAdmin) return true;
        return await _db.LibraryGrants.AnyAsync(g => g.UserId == userId && g.LibraryId == libraryId, ct);
    }
}
