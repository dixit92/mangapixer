namespace com.lifepixer.mangapixer.Server.Features.Metadata.Flags;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// "Wrong series?" flags (metadata stage 2). Any signed-in user with access to a
/// node can flag the series information it shows (404 without access, never 403);
/// admins list and resolve flags. Notes are user content: admin-only, never logged.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class MetadataFlagsController : ControllerBase
{
    [HttpPost("nodes/{nodeId}/series-info/flags")]
    [ProducesResponseType<MetadataMyFlagDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    public IActionResult Create(string nodeId, [FromBody] CreateMetadataFlagRequest request) => NotImplemented();

    [HttpGet("nodes/{nodeId}/series-info/flags/mine")]
    [ProducesResponseType<MetadataMyFlagStateDto>(StatusCodes.Status200OK)]
    public IActionResult Mine(string nodeId) => NotImplemented();

    /// <summary>Flags by <paramref name="state"/>: <c>open</c> (default, auto links first), <c>resolved</c> or <c>all</c>.</summary>
    [HttpGet("admin/metadata/flags")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType<MetadataFlagPageDto>(StatusCodes.Status200OK)]
    public IActionResult List([FromQuery] string? state = null, [FromQuery] string? library = null,
        [FromQuery] string? cursor = null, [FromQuery] int limit = 50) => NotImplemented();

    [HttpPost("admin/metadata/flags/{flagId}/resolve")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType<MetadataFlagDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public IActionResult Resolve(string flagId, [FromBody] ResolveMetadataFlagRequest request) => NotImplemented();

    private ObjectResult NotImplemented() =>
        StatusCode(StatusCodes.Status501NotImplemented, new ApiError { Error = "not_implemented", Message = "Not implemented yet." });
}
