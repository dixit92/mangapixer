namespace com.lifepixer.mangapixer.Server.Features.Export;

using System.Globalization;
using System.Security.Claims;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Tokens;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Scanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// <c>POST /api/v1/export/libraries/{id}/scan</c> (1.36.0, for MangaList): a personal access token with the <c>library:scan</c>
/// scope asks for a FULL scan of one library - the same scan as Administration's "Scan" (<see cref="LibraryScanLauncher"/>), never a
/// single-folder scan, and nothing else: no content, link or setting changes, no network request. <c>{id}</c> is the library's id
/// as <c>GET /api/v1/export/libraries</c> lists it; a token may scan any library the export shows (every library).
/// </summary>
/// <remarks>
/// <para>Security (owner-approved design, 1.36.0): <see cref="ExportApi.ScanPolicy"/> authenticates with the token scheme ONLY, so
/// an admin's browser cookie is never a credential here (a cookie POST gets 401). That is why this one action skips the global
/// antiforgery check; the token scheme accepts the POST only because the action carries <see cref="TokenWriteAllowedAttribute"/>.
/// Do not add either attribute anywhere else without owner approval.</para>
/// <para>Answers: 202 <see cref="ScanTriggeredDto"/>; 404 <c>library_not_found</c>; 409 <c>scan_in_progress</c> + <c>Retry-After: 60</c>
/// while a scan of that library runs (the client retries; a running scan may already have passed the new files); 429
/// <c>scan_cooldown</c> + <c>Retry-After</c> inside the per-library cooldown (<see cref="TokenScanCooldown"/>); 401 without a valid
/// token; 403 for a token without the scope. Each started scan writes one audit row (<see cref="AuditActions.LibraryScanRequest"/>:
/// actor = the token's owner, target = the library, correlation = the token's public id); refusals are logged at Debug only, so a
/// client retrying in a loop neither floods the log nor the audit trail.</para>
/// </remarks>
[ApiController]
[Route(ExportApi.RoutePrefix)]
[Authorize(Policy = ExportApi.ScanPolicy)]
public sealed class ExportScanController(
    MangaPixerDbContext db,
    LibraryScanLauncher launcher,
    TokenScanCooldown cooldown,
    AuditService audit,
    ILogger<ExportScanController> logger) : ControllerBase
{
    /// <summary>The <c>Retry-After</c> of a 409 (owner decision: the client asks again in a minute).</summary>
    public const int InProgressRetryAfterSeconds = 60;

    /// <summary>The scan lease owner of a token-requested scan: <c>token:&lt;public id&gt;</c> (never the secret).</summary>
    public static string LeaseOwnerOf(string tokenPublicId) => "token:" + tokenPublicId;

    [HttpPost("libraries/{id}/scan")]
    [TokenWriteAllowed]
    [IgnoreAntiforgeryToken] // Safe ONLY because ExportApi.ScanPolicy never accepts the cookie (see the class remarks).
    [ProducesResponseType<ScanTriggeredDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(void), StatusCodes.Status401Unauthorized)] // the token scheme's challenge, no body
    [ProducesResponseType(typeof(void), StatusCodes.Status403Forbidden)]    // the scan scope is missing, no body
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Scan(string id, CancellationToken ct)
    {
        // Both claims exist on every principal the token scheme builds; the policy admits no other principal.
        var tokenId = User.FindFirstValue(ApiTokenClaims.TokenId);
        if (tokenId is null
            || !long.TryParse(User.FindFirstValue(ApiTokenClaims.OwnerUserId), NumberStyles.None, CultureInfo.InvariantCulture, out var ownerId))
            return Forbid();

        var library = await db.Libraries.FirstOrDefaultAsync(l => l.PublicId == id, ct);
        if (library is null)
        {
            logger.LogDebug(LogEvents.Auth.ApiTokenScanRefused, "Scan request by API token {TokenId} refused: {Reason} ({Status})",
                tokenId, "library_not_found", StatusCodes.Status404NotFound);
            return NotFound(new ApiError { Error = "library_not_found", Message = "There is no library with this id." });
        }

        var (outcome, launch, wait) = await cooldown.TryStartAsync(
            library.Id, token => launcher.StartAsync(library, LeaseOwnerOf(tokenId), token), ct);

        switch (outcome)
        {
            case TokenScanStart.InProgress:
                logger.LogDebug(LogEvents.Auth.ApiTokenScanRefused, "Scan request by API token {TokenId} for library {LibraryId} refused: {Reason} ({Status})",
                    tokenId, library.Id, "scan_in_progress", StatusCodes.Status409Conflict);
                Response.Headers.RetryAfter = InProgressRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return Conflict(new ApiError { Error = "scan_in_progress", Message = "A scan of this library is already running. Try again later." });

            case TokenScanStart.CoolingDown:
                var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
                logger.LogDebug(LogEvents.Auth.ApiTokenScanRefused, "Scan request by API token {TokenId} for library {LibraryId} refused: {Reason} ({Status}); retry in {RetryAfter}s",
                    tokenId, library.Id, "scan_cooldown", StatusCodes.Status429TooManyRequests, seconds);
                Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError
                {
                    Error = "scan_cooldown",
                    Message = "This library was scanned on request a short while ago. Try again later.",
                });
        }

        var runId = OpaqueId.Encode(launch!.Run.Id);
        logger.LogInformation(LogEvents.Auth.ApiTokenScanStarted, "Scan {ScanRunId} of library {LibraryId} started on request of API token {TokenId}",
            runId, library.Id, tokenId);
        // The audit actor is the token's owner (resolved by name, as AuditService records every actor). Not cancellable: the scan
        // runs now, so its audit row must land even if the client hangs up.
        var ownerName = await db.Users.AsNoTracking().Where(u => u.Id == ownerId).Select(u => u.UserName).FirstOrDefaultAsync(CancellationToken.None);
        await audit.RecordAsync(AuditActions.LibraryScanRequest, AuditResults.Success, ownerName,
            correlationId: tokenId, ct: CancellationToken.None, targetLibraryId: library.Id);
        return Accepted(new ScanTriggeredDto { ScanRunId = runId });
    }
}
