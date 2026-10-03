namespace com.lifepixer.mangapixer.Server.Features.Export;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Tokens;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// <c>GET /api/v1/export/ping</c> (1.33.0): a client's "test connection" for the export. Answers which credential was accepted
/// (a personal access token or an admin's cookie) and the server's clock; it reads nothing else.
/// </summary>
[ApiController]
[Route(ExportApi.RoutePrefix)]
[Authorize(Policy = ExportApi.Policy)]
public sealed class ExportPingController(TimeProvider clock) : ControllerBase
{
    [HttpGet("ping")]
    [HttpHead("ping")]
    [ProducesResponseType<ExportPingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    public IActionResult Ping() => Ok(new ExportPingDto
    {
        Ok = true,
        ServerTime = clock.GetUtcNow(),
        Auth = User.HasClaim(c => c.Type == ApiTokenClaims.TokenId) ? "token" : "cookie",
    });
}
