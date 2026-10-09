namespace com.lifepixer.mangapixer.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The layered card cover of any node and the admin cover picker (1.29.0 cover layer).
/// <list type="bullet">
/// <item><c>GET /nodes/{nodeId}/cover?v=</c> - the node's CURRENT resolved cover (choice > pin > automatic > file), for anyone
/// with access to its library; the current token caches for a year, any other revalidates (<see cref="CoverCaching"/>).</item>
/// <item>Admin: <c>GET /nodes/{nodeId}/cover-options</c>, <c>PUT</c> / <c>DELETE /nodes/{nodeId}/cover-choice</c>,
/// <c>GET /nodes/{archiveId}/cover-crops/{left|right}</c> and <c>GET /nodes/{folderId}/cover-poster</c> (picker previews).</item>
/// </list>
/// Web covers reach non-admins only through a node (library access + the "Show saved web covers" rule). Every layered
/// image falls back to the node's file thumbnail when its own file is missing. No path, title or name is logged.
/// </summary>
[ApiController]
[Route("api/v1/nodes/{nodeId}")]
[Authorize]
public sealed class NodeCoverController : ControllerBase
{
    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _libraryAuth;
    private readonly CoverResolutionService _resolutions;
    private readonly CoverPickerService _picker;
    private readonly CoverCropService _crops;
    private readonly CoverFiles _files;
    private readonly ThumbnailStore _thumbnails;
    private readonly MetadataImageStore _posters;
    private readonly ILogger<NodeCoverController> _logger;

    public NodeCoverController(
        MangaPixerDbContext db,
        LibraryAuthorizationService libraryAuth,
        CoverResolutionService resolutions,
        CoverPickerService picker,
        CoverCropService crops,
        CoverFiles files,
        ThumbnailStore thumbnails,
        MetadataImageStore posters,
        ILogger<NodeCoverController> logger)
    {
        _db = db;
        _libraryAuth = libraryAuth;
        _resolutions = resolutions;
        _picker = picker;
        _crops = crops;
        _files = files;
        _thumbnails = thumbnails;
        _posters = posters;
        _logger = logger;
    }

    /// <summary>The node's resolved cover image (202 <c>pending</c> while its file thumbnail is being generated).</summary>
    [HttpGet("cover")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> GetCover(string nodeId, [FromQuery] string? v, CancellationToken ct)
    {
        if (GetUserId() is not { } userId)
            return Unauthorized();
        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodeId, ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return NotFound();
        if (!await _libraryAuth.CanAccessLibraryAsync(userId, node.LibraryId, ct))
            return NotFound();

        var resolved = await _resolutions.ResolveOneAsync(node, ct);
        if (resolved is null)
            return NotFound();

        Stream? stream = null;
        var contentType = "image/webp";
        switch (resolved.Image)
        {
            case CoverImageKind.Crop when resolved.CropSide is { } side:
                stream = CoverFiles.OpenRead(_files.CropPath(resolved.ArchiveNodeId, resolved.ContentVersion, side));
                if (stream is null && await _crops.EnsureAsync(resolved.ArchiveNodeId, side, rerender: false, ct) is { } crop)
                    stream = CoverFiles.OpenRead(crop.Path);
                break;
            case CoverImageKind.VolumeCover when resolved.VolumeCoverPublicId is { } coverId:
                stream = CoverFiles.OpenRead(_files.VolumeCoverPath(coverId, resolved.StoredVersion));
                break;
            case CoverImageKind.Poster when resolved.RecordId is { } recordId:
                if (_posters.Open(recordId, resolved.ImageVersion) is { } poster)
                    (stream, contentType) = poster;
                break;
        }

        if (stream is not null)
        {
            if (CoverCaching.Apply(Response, Request, "cov-" + resolved.Version, string.Equals(v, resolved.Version, StringComparison.Ordinal)))
            {
                await stream.DisposeAsync();
                return StatusCode(StatusCodes.Status304NotModified);
            }
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(stream, contentType);
        }

        // The file default, or the fallback of a layered image whose own file is missing (served without long caching).
        if (resolved.ArchiveNodeId == 0)
            return NotFound();
        var fileIsTheCover = resolved.Image == CoverImageKind.File;
        if (_thumbnails.OpenRead(resolved.ArchiveNodeId, resolved.ContentVersion) is { } thumb)
        {
            var etag = fileIsTheCover ? "cov-" + resolved.Version : $"thumb-{resolved.ArchiveNodeId}-{resolved.ContentVersion}";
            if (CoverCaching.Apply(Response, Request, etag, fileIsTheCover && string.Equals(v, resolved.Version, StringComparison.Ordinal)))
            {
                await thumb.DisposeAsync();
                return StatusCode(StatusCodes.Status304NotModified);
            }
            return File(thumb, "image/webp");
        }

        QueueThumbnail(resolved.ArchiveNodeId);
        return Accepted(new ApiError { Error = "pending", Message = "Thumbnail is being generated." });
    }

    /// <summary>A half of an archive's page 1 (picker preview), rendered on demand into the crop store. Admin only.</summary>
    [HttpGet("cover-crops/{side}")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCrop(string nodeId, string side, CancellationToken ct)
    {
        CoverCropSide cropSide;
        if (string.Equals(side, "left", StringComparison.Ordinal))
            cropSide = CoverCropSide.Left;
        else if (string.Equals(side, "right", StringComparison.Ordinal))
            cropSide = CoverCropSide.Right;
        else
            return BadRequest(new ApiError { Error = "invalid_request", Message = "side must be left or right." });

        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodeId, ct);
        if (node is null || node.Kind != (int)CatalogNodeKind.Archive || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return NotFound();
        var crop = await _crops.EnsureAsync(node.Id, cropSide, rerender: false, ct);
        if (crop is null || CoverFiles.OpenRead(crop.Path) is not { } stream)
            return NotFound(new ApiError { Error = "crop_unavailable", Message = "The page could not be cropped." });
        Response.Headers.CacheControl = CoverCaching.Revalidate;
        return File(stream, "image/webp");
    }

    /// <summary>
    /// 1.39.0: the picker preview of a folder's linked series poster (admin). Served from the stored poster only - never a request;
    /// 404 when the folder is not linked to a series record with a stored poster. Not behind "series information hidden": the cover
    /// choice has its own rules.
    /// </summary>
    [HttpGet("cover-poster")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPoster(string nodeId, CancellationToken ct)
    {
        var record = await _picker.PosterRecordOfAsync(nodeId, ct);
        if (record is null || _posters.Open(record.Id, record.ImageVersion) is not { } poster)
            return NotFound();
        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(poster.Stream, poster.ContentType);
    }

    [HttpGet("cover-options")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType<CoverOptionsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOptions(string nodeId, CancellationToken ct)
    {
        var options = await _picker.GetOptionsAsync(nodeId, ct);
        return options is null ? NotFound() : Ok(options);
    }

    [HttpPut("cover-choice")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType<CoverStateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PutChoice(string nodeId, [FromBody] CoverChoiceRequest request, CancellationToken ct)
    {
        var (result, state) = await _picker.SetChoiceAsync(nodeId, request, User.Identity?.Name, GetUserId(), ct);
        return ToResult(result, state);
    }

    /// <summary>Back to Automatic.</summary>
    [HttpDelete("cover-choice")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType<CoverStateDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteChoice(string nodeId, CancellationToken ct)
    {
        var (result, state) = await _picker.ClearChoiceAsync(nodeId, User.Identity?.Name, ct);
        return ToResult(result, state);
    }

    private IActionResult ToResult(CoverChoiceResult result, CoverStateDto? state) => result switch
    {
        CoverChoiceResult.Ok => Ok(state),
        CoverChoiceResult.NotFound => NotFound(),
        CoverChoiceResult.NotStored => Conflict(new ApiError { Error = "cover_not_stored", Message = "This cover has not been downloaded yet." }),
        CoverChoiceResult.NoArchive => BadRequest(new ApiError { Error = "no_archive", Message = "There is no page to crop here." }),
        _ => BadRequest(new ApiError { Error = "invalid_cover_choice", Message = "The cover choice is not valid for this item." }),
    };

    /// <summary>Generate-on-miss safety net (as the file cover endpoint): a retry finds the thumbnail.</summary>
    private void QueueThumbnail(long archiveNodeId)
    {
        var services = HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = services.CreateScope();
                if (scope.ServiceProvider.GetService<ThumbnailGenerationService>() is { } thumbnails)
                    await thumbnails.GenerateForItemAsync(archiveNodeId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(LogEvents.Worker.ThumbnailGenerationFailed, "Generate-on-miss failed (item {ItemId}): {Error}", archiveNodeId, ex.GetType().Name);
            }
        }, CancellationToken.None);
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }
}
