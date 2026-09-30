namespace com.lifepixer.mangapixer.Server.Features.Metadata.Reach;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// The Official releases tab of Metadata Manager (1.30.0). Admin-only; built from stored data, never a provider request.
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata/official-releases")]
[Authorize(Policy = "Admin")]
public sealed class OfficialReleasesController(OfficialReleasesService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<OfficialReleasesPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List([FromQuery] string? library = null, [FromQuery] OfficialReleasesFilter filter = OfficialReleasesFilter.ToAct,
        [FromQuery] string? cursor = null, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var (error, page) = await service.ListAsync(library, filter, cursor, limit, ct);
        return error is null ? Ok(page) : NotFound();
    }
}
