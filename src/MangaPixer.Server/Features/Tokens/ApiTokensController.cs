namespace com.lifepixer.mangapixer.Server.Features.Tokens;

using System.Globalization;
using System.Security.Claims;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Administration of personal access tokens (1.33.0): list, create (the secret is answered once), revoke. Admin cookie only (the
/// default scheme; a token cannot reach these routes), CSRF-checked like every unsafe admin call. Audit rows and logs carry the
/// token's public id only.
/// </summary>
[ApiController]
[Route("api/v1/admin/tokens")]
[Authorize(Policy = "Admin")]
public sealed class ApiTokensController(ApiTokenService tokens, AuditService audit, ILogger<ApiTokensController> logger) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ApiTokenDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await tokens.ListAsync(ct));

    [HttpPost]
    [ProducesResponseType<CreateApiTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateApiTokenRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > ApiTokenLimits.MaxNameLength || name.Any(char.IsControl))
            return BadRequest(new ApiError { Error = "invalid_name", Message = "Give the token a name of 1 to 64 characters." });
        if (request.ExpiresInDays is { } days && !ApiTokenLimits.AllowedExpiryDays.Contains(days))
            return BadRequest(new ApiError { Error = "invalid_expiry", Message = "A token expires after 30, 90 or 365 days, or never." });
        // 1.36.0: scopes are chosen here and never later; absent = the read scope only (1.33.0 behaviour).
        if (!ApiTokenService.TryNormalizeScopes(request.Scopes, out var scopes))
            return BadRequest(new ApiError { Error = "invalid_scope", Message = "Choose at least one of: read the metadata export, request library scans." });
        if (!long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
            return Forbid();

        var created = await tokens.CreateAsync(userId, name, request.ExpiresInDays, scopes, ct);
        logger.LogInformation(LogEvents.Auth.ApiTokenCreated, "API token {TokenId} created (expires in {ExpiresInDays} days, scopes {Scopes})",
            created.Token.Id, request.ExpiresInDays?.ToString(CultureInfo.InvariantCulture) ?? "never", string.Join(' ', scopes));
        await audit.RecordAsync(AuditActions.ApiTokenCreate, AuditResults.Success, User.Identity?.Name,
            targetUserId: userId, correlationId: created.Token.Id, ct);
        return Ok(created);
    }

    [HttpPost("{id}/revoke")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(string id, CancellationToken ct)
    {
        var revoked = await tokens.RevokeAsync(id, ct);
        if (revoked is null)
            return NotFound(new ApiError { Error = "token_not_found", Message = "There is no token with this id." });
        logger.LogInformation(LogEvents.Auth.ApiTokenRevoked, "API token {TokenId} revoked", revoked.PublicId);
        await audit.RecordAsync(AuditActions.ApiTokenRevoke, AuditResults.Success, User.Identity?.Name,
            targetUserId: revoked.UserId, correlationId: revoked.PublicId, ct);
        return NoContent();
    }
}
