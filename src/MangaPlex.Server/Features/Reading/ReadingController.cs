namespace com.lifepixer.mangaplex.Server.Features.Reading;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Features.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Reading API endpoints: progress, bookmarks, preferences, continue-reading.
/// All endpoints require authentication.
/// </summary>
[ApiController]
[Route("api/v1/reading")]
[Authorize]
public sealed class ReadingController : ControllerBase
{
    private readonly ReadingStateService _stateService;
    private readonly CatalogIdResolver _idResolver;
    private readonly ILogger<ReadingController> _logger;

    public ReadingController(
        ReadingStateService stateService,
        CatalogIdResolver idResolver,
        ILogger<ReadingController> logger)
    {
        _stateService = stateService;
        _idResolver = idResolver;
        _logger = logger;
    }

    [HttpGet("progress/{itemId}")]
    public async Task<IActionResult> GetProgress(string itemId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        // Resolve public ID to internal ID (audit defect D5/D29)
        var node = await _idResolver.ResolveNodeAsync(itemId, ct);
        if (node is null) return NotFound();

        var progress = await _stateService.GetProgressAsync(userId.Value, node.Id, ct);
        if (progress is null) return NotFound();

        return Ok(progress);
    }

    [HttpPut("progress/{itemId}")]
    public async Task<IActionResult> UpdateProgress(
        string itemId,
        [FromBody] UpdateProgressRequest request,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        // Resolve public ID to internal ID (audit defect D5/D29)
        var node = await _idResolver.ResolveNodeAsync(itemId, ct);
        if (node is null) return NotFound();

        var result = await _stateService.UpdateProgressAsync(
            userId.Value,
            node.Id,
            request.PageIndex,
            request.ExpectedContentVersion,
            mutationId: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ct: ct);

        return result.Status switch
        {
            UpdateStatus.Success => Ok(new { revision = result.Revision, alreadyApplied = result.AlreadyApplied }),
            UpdateStatus.Unauthorized => Unauthorized(new ApiError { Error = "unauthorized", Message = result.Error ?? "Access denied" }),
            UpdateStatus.NotFound => NotFound(new ApiError { Error = "not_found", Message = result.Error ?? "Item not found" }),
            UpdateStatus.StaleContent => Conflict(new ApiError { Error = "stale_content", Message = result.Error ?? "Content version mismatch" }),
            UpdateStatus.Invalid => BadRequest(new ApiError { Error = "invalid", Message = result.Error ?? "Invalid request" }),
            _ => StatusCode(500, new ApiError { Error = "internal_error", Message = "Unexpected error" }),
        };
    }

    [HttpDelete("progress/{itemId}")]
    public async Task<IActionResult> ResetProgress(string itemId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        // Resolve public ID to internal ID (audit defect D5/D29)
        var node = await _idResolver.ResolveNodeAsync(itemId, ct);
        if (node is null) return NotFound();

        var success = await _stateService.ResetProgressAsync(userId.Value, node.Id, ct);
        if (!success) return Unauthorized();

        return NoContent();
    }

    [HttpGet("continue")]
    public async Task<IActionResult> GetContinueReading(
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var entries = await _stateService.GetContinueReadingAsync(userId.Value, limit, ct);
        return Ok(entries);
    }

    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var prefs = await _stateService.GetPreferencesAsync(userId.Value, ct);
        return Ok(prefs);
    }

    [HttpPut("preferences")]
    public async Task<IActionResult> SetPreferences(
        [FromBody] UserPreferencesDto request,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        await _stateService.SetPreferencesAsync(userId.Value, request, ct);
        return NoContent();
    }

    [HttpGet("{itemId}/bookmarks")]
    public async Task<IActionResult> GetBookmarks(string itemId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        // Resolve public ID to internal ID (audit defect D5/D29)
        var node = await _idResolver.ResolveNodeAsync(itemId, ct);
        if (node is null) return NotFound();

        var bookmarks = await _stateService.GetBookmarksAsync(userId.Value, node.Id, ct);
        return Ok(bookmarks);
    }

    [HttpPost("{itemId}/bookmarks")]
    public async Task<IActionResult> AddBookmark(
        string itemId,
        [FromBody] AddBookmarkRequest request,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        // Resolve public ID to internal ID (audit defect D5/D29)
        var node = await _idResolver.ResolveNodeAsync(itemId, ct);
        if (node is null) return NotFound();

        var bookmarkId = await _stateService.AddBookmarkAsync(
            userId.Value, node.Id, request.Ordinal, request.NormalizedAnchor, request.Label, ct);
        if (bookmarkId is null) return Unauthorized();

        return Ok(new { id = bookmarkId });
    }

    [HttpDelete("bookmarks/{bookmarkId}")]
    public async Task<IActionResult> RemoveBookmark(string bookmarkId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var success = await _stateService.RemoveBookmarkAsync(userId.Value, bookmarkId, ct);
        if (!success) return NotFound();

        return NoContent();
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var id))
            return null;
        return id;
    }
}

/// <summary>
/// Request to add a bookmark.
/// </summary>
public sealed record AddBookmarkRequest
{
    public required int Ordinal { get; init; }
    public double NormalizedAnchor { get; init; }
    public string? Label { get; init; }
}
