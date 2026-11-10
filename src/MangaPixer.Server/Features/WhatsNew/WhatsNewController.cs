namespace com.lifepixer.mangapixer.Server.Features.WhatsNew;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// "What's new" for admins (1.53.0): the release notes the signed-in admin has not seen since their last version
/// (<c>users.LastSeenVersion</c>), and marking them seen. Admin policy (a reader gets 403, anonymous 401). Reads only the notes embedded in
/// the server and the admin's own user row; no request leaves the server.
/// </summary>
[ApiController]
[Route("api/v1/admin/whats-new")]
[Authorize(Policy = "Admin")]
public sealed class WhatsNewController : ControllerBase
{
    private readonly WhatsNewNotes _notes;
    private readonly MangaPixerDbContext _db;
    private readonly ILogger<WhatsNewController> _logger;

    public WhatsNewController(WhatsNewNotes notes, MangaPixerDbContext db, ILogger<WhatsNewController> logger)
    {
        _notes = notes;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// The sections newer than the version this admin last saw and not newer than the running one, newest first; with
    /// <c>?current=true</c>, the running version's section whether seen or not (the account menu's "What's new"). Never the
    /// <c>[Unreleased]</c> section; empty when nothing is new or the notes cannot be read.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(WhatsNewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<WhatsNewDto>> Get([FromQuery] bool current, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var lastSeen = await _db.Users.AsNoTracking().Where(u => u.Id == userId.Value).Select(u => u.LastSeenVersion).SingleOrDefaultAsync(ct);
        return Ok(_notes.For(lastSeen, current));
    }

    /// <summary>Records that this admin has seen the running version's notes (sets their last seen version to it). Idempotent; 204.</summary>
    [HttpPost("seen")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkSeen(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var current = _notes.Current;
        if (current is not null)
        {
            await _db.Users.Where(u => u.Id == userId.Value).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenVersion, current), ct);
            _logger.LogDebug(LogEvents.Administration.WhatsNewSeen, "What's new: user {UserId} saw the notes of {Version}", userId.Value, current);
        }
        return NoContent();
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }
}
