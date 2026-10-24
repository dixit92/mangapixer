namespace com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// The MangaDex records the latest-upload sweep stopped finding (1.47.0, owner decision 10a): admin-only, read from stored rows, never a
/// provider request. Cookie + CSRF only (API tokens do not reach it).
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata/taken-down")]
[Authorize(Policy = "Admin")]
public sealed class TakenDownRecordsController(TakenDownRecordsService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<TakenDownRecordsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await service.ListAsync(ct));
}
