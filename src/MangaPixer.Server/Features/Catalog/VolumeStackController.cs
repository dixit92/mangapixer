namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Security.Claims;
using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Volumes view endpoints (1.29.0): the folder's view state (drives the Volumes | Folders switch) and one virtual volume
/// stack. Any authenticated user with access to the library; reads stored data only.
/// </summary>
[ApiController]
[Route("api/v1/nodes/{nodeId}")]
[Authorize]
public sealed class VolumeStackController(VolumeStackService service, CatalogIdResolver resolver) : ControllerBase
{
    [HttpGet("volume-view")]
    [ProducesResponseType<VolumeViewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetView(string nodeId, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Unauthorized();
        var node = await resolver.ResolveNodeAsync(nodeId, ct);
        if (node is null)
            return NotFound();
        var view = await service.GetViewAsync(userId, node.Id, ct);
        return view is null ? NotFound() : Ok(view);
    }

    [HttpGet("volumes/{key}")]
    [ProducesResponseType<VolumeStackDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStack(string nodeId, string key, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Unauthorized();
        var node = await resolver.ResolveNodeAsync(nodeId, ct);
        if (node is null)
            return NotFound();
        var stack = await service.GetStackAsync(userId, node.Id, key, ct);
        return stack is null ? NotFound() : Ok(stack);
    }

    private long? UserId() => long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
