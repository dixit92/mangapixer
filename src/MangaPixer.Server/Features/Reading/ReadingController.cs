namespace com.lifepixer.mangapixer.Server.Features.Reading;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
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
    private readonly ReaderModeResolver _modeResolver;
    private readonly IncognitoAccessor _incognito;
    private readonly ILogger<ReadingController> _logger;

    public ReadingController(
        ReadingStateService stateService,
        CatalogIdResolver idResolver,
        ReaderModeResolver modeResolver,
        IncognitoAccessor incognito,
        ILogger<ReadingController> logger)
    {
        _stateService = stateService;
        _idResolver = idResolver;
        _modeResolver = modeResolver;
        _incognito = incognito;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the effective default reader mode for an item (1.2.0): per-user item
    /// override → nearest folder default → library default → user default → PagedLtr.
    /// </summary>
    [HttpGet("{itemId}/effective-mode")]
    public async Task<IActionResult> GetEffectiveMode(string itemId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var node = await _idResolver.ResolveNodeAsync(itemId, ct);
        if (node is null) return NotFound();

        var mode = await _modeResolver.ResolveAsync(userId.Value, node, ct);
        return Ok(new EffectiveReaderModeDto { ReaderMode = mode });
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

        // Set ETag header so clients can use If-Match on subsequent PUTs
        // (audit defect D32).
        Response.Headers.ETag = $"\"{progress.Revision}\"";

        return Ok(progress);
    }

    [HttpPut("progress/{itemId}")]
    public async Task<IActionResult> UpdateProgress(
        string itemId,
        [FromBody] UpdateProgressRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromHeader(Name = "If-None-Match")] string? ifNoneMatch,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        // Resolve public ID to internal ID (audit defect D5/D29)
        var node = await _idResolver.ResolveNodeAsync(itemId, ct);
        if (node is null) return NotFound();

        // Parse If-Match revision (audit defect D32)
        // If-Match: "<revision>" or If-None-Match: * for first write
        long? expectedRevision = null;
        if (ifNoneMatch == "*")
        {
            expectedRevision = null; // First write — no precondition
        }
        else if (ifMatch is not null)
        {
            // Strip quotes and parse
            var revStr = ifMatch.Trim('"');
            if (long.TryParse(revStr, out var rev))
                expectedRevision = rev;
        }

        var result = await _stateService.UpdateProgressAsync(
            userId.Value,
            node.Id,
            request.PageIndex,
            request.ExpectedContentVersion,
            mutationId: request.MutationId,
            expectedRevision: expectedRevision,
            normalizedAnchor: request.NormalizedAnchor,
            ct: ct);

        return result.Status switch
        {
            UpdateStatus.Success => Ok(new { revision = result.Revision, alreadyApplied = result.AlreadyApplied }),
            UpdateStatus.Unauthorized => Unauthorized(new ApiError { Error = "unauthorized", Message = result.Error ?? "Access denied" }),
            UpdateStatus.NotFound => NotFound(new ApiError { Error = "not_found", Message = result.Error ?? "Item not found" }),
            UpdateStatus.StaleContent => Conflict(new ApiError { Error = "stale_content", Message = result.Error ?? "Content version mismatch" }),
            UpdateStatus.PreconditionFailed => StatusCode(412, new ApiError { Error = "precondition_failed", Message = result.Error ?? "Revision mismatch" }),
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

    // --- Sticky read-marks (1.2.0) ---

    /// <summary>
    /// Gets the current user's sticky read-mark state for an item.
    /// </summary>
    [HttpGet("{itemId}/read")]
    public async Task<IActionResult> GetReadMark(string itemId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var node = await _idResolver.ResolveNodeAsync(itemId, ct);
        if (node is null) return NotFound();

        var isRead = await _stateService.IsReadAsync(userId.Value, node.Id, ct);
        return Ok(new ReadMarkDto { ItemId = itemId, IsRead = isRead });
    }

    /// <summary>
    /// Marks an item read (sticky). Idempotent.
    /// </summary>
    [HttpPut("{itemId}/read")]
    public async Task<IActionResult> SetReadMark(string itemId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var node = await _idResolver.ResolveNodeAsync(itemId, ct);
        if (node is null) return NotFound();

        var ok = await _stateService.SetItemReadAsync(userId.Value, node.Id, read: true, ct);
        if (!ok) return Unauthorized();

        return Ok(new ReadMarkDto { ItemId = itemId, IsRead = true });
    }

    /// <summary>
    /// Clears an item's read-mark (marks it unread). Idempotent.
    /// </summary>
    [HttpDelete("{itemId}/read")]
    public async Task<IActionResult> ClearReadMark(string itemId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var node = await _idResolver.ResolveNodeAsync(itemId, ct);
        if (node is null) return NotFound();

        var ok = await _stateService.SetItemReadAsync(userId.Value, node.Id, read: false, ct);
        if (!ok) return Unauthorized();

        return Ok(new ReadMarkDto { ItemId = itemId, IsRead = false });
    }

    /// <summary>
    /// Marks every readable descendant archive of a folder read (bulk, sticky).
    /// </summary>
    [HttpPut("folders/{nodeId}/read")]
    public async Task<IActionResult> SetFolderReadMark(string nodeId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var node = await _idResolver.ResolveNodeAsync(nodeId, ct);
        if (node is null) return NotFound();

        var result = await _stateService.SetFolderReadAsync(userId.Value, node.Id, read: true, ct);
        if (result is null) return NotFound();

        return Ok(result);
    }

    /// <summary>
    /// Clears the read-mark on every descendant archive of a folder (bulk).
    /// </summary>
    [HttpDelete("folders/{nodeId}/read")]
    public async Task<IActionResult> ClearFolderReadMark(string nodeId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var node = await _idResolver.ResolveNodeAsync(nodeId, ct);
        if (node is null) return NotFound();

        var result = await _stateService.SetFolderReadAsync(userId.Value, node.Id, read: false, ct);
        if (result is null) return NotFound();

        return Ok(result);
    }

    /// <summary>
    /// Dismisses an item from the current user's continue-reading strip (1.2.0),
    /// without marking it read. Reappears if the user reads it again.
    /// </summary>
    [HttpDelete("continue/{itemId}")]
    public async Task<IActionResult> DismissContinue(string itemId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var node = await _idResolver.ResolveNodeAsync(itemId, ct);
        if (node is null) return NotFound();

        var ok = await _stateService.DismissFromContinueAsync(userId.Value, node.Id, ct);
        if (!ok) return Unauthorized();

        return NoContent();
    }

    [HttpGet("continue")]
    public async Task<IActionResult> GetContinueReading(
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var entries = await _stateService.GetContinueReadingAsync(
            userId.Value, limit, incognito: _incognito.IsIncognito, ct: ct);
        return Ok(entries);
    }

    /// <summary>
    /// Gets continue-reading items for a user within a single library (1.4.0
    /// sidebar grouping). Respects Incognito mode: a Private library returns
    /// empty while Incognito is active.
    /// </summary>
    [HttpGet("continue/by-library/{libraryId}")]
    public async Task<IActionResult> GetContinueReadingByLibrary(
        string libraryId,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var library = await _idResolver.ResolveLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        var entries = await _stateService.GetContinueReadingByLibraryAsync(
            userId.Value, library.Id, limit, incognito: _incognito.IsIncognito, ct: ct);
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

    /// <summary>
    /// Gets the current user's library browse presentation preferences (1.2.0).
    /// </summary>
    [HttpGet("library-preferences")]
    public async Task<IActionResult> GetLibraryPreferences(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var prefs = await _stateService.GetLibraryPreferencesAsync(userId.Value, ct);
        return Ok(prefs);
    }

    /// <summary>
    /// Sets the current user's library browse presentation preferences (1.2.0).
    /// </summary>
    [HttpPut("library-preferences")]
    public async Task<IActionResult> SetLibraryPreferences(
        [FromBody] LibraryViewPreferencesDto request,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        await _stateService.SetLibraryPreferencesAsync(userId.Value, request, ct);
        return NoContent();
    }

    /// <summary>
    /// Gets the current user's Private library designations (1.4.0) as public IDs.
    /// </summary>
    [HttpGet("private-libraries")]
    public async Task<IActionResult> GetPrivateLibraries(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var prefs = await _stateService.GetPrivateLibrariesAsync(userId.Value, ct);
        return Ok(prefs);
    }

    /// <summary>
    /// Replaces the current user's Private library set (1.4.0). The entire list
    /// is replaced on each call. Unknown library IDs are silently skipped.
    /// </summary>
    [HttpPut("private-libraries")]
    public async Task<IActionResult> SetPrivateLibraries(
        [FromBody] SetPrivateLibrariesRequest request,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        await _stateService.SetPrivateLibrariesAsync(userId.Value, request.LibraryIds, ct);
        return NoContent();
    }

    /// <summary>
    /// Gets the current user's home-excluded libraries (1.12.0) as public IDs. Libraries in
    /// this set are hidden from the home "New chapters" surface.
    /// </summary>
    [HttpGet("home-libraries")]
    public async Task<IActionResult> GetHomeLibraries(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var prefs = await _stateService.GetHomeLibrariesAsync(userId.Value, ct);
        return Ok(prefs);
    }

    /// <summary>
    /// Replaces the current user's home-excluded library set (1.12.0). The entire list is
    /// replaced on each call; ids the user cannot access are silently skipped.
    /// </summary>
    [HttpPut("home-libraries")]
    public async Task<IActionResult> SetHomeLibraries(
        [FromBody] HomeLibraryVisibilityDto request,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        await _stateService.SetHomeLibrariesAsync(userId.Value, request.ExcludedLibraryIds, ct);
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
