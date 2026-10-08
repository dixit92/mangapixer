namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Security.Claims;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Stacks of stories collected in one volume (1.37.0, "tankoubon stacks"): one stack's stories, and its cover. Any authenticated user
/// with access to the folder's library; reads stored data only - the browser never contacts a provider.
/// </summary>
[ApiController]
[Route("api/v1/nodes/{nodeId}/collection-stacks/{key}")]
[Authorize]
public sealed class CollectionStackController(
    CollectionStackService service, CatalogIdResolver resolver, ICoverResolver covers, MetadataImageStore images) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<CollectionStackDto>(StatusCodes.Status200OK)]
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

    /// <summary>
    /// The stack's cover: the record's stored poster while web covers are shown here (the current version cached a year, <c>private</c>;
    /// any other version revalidates), else a redirect, uncached, to the first story's current cover.
    /// </summary>
    [HttpGet("cover")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> GetCover(string nodeId, string key, [FromQuery] string? v, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Unauthorized();
        var node = await resolver.ResolveNodeAsync(nodeId, ct);
        if (node is null || await service.FindAsync(userId, node.Id, key, ct) is not { } found)
            return NotFound();
        var (view, collection, head) = found;

        if (head?.Poster is { } poster && images.Open(poster.RecordId, poster.ImageVersion) is { } image)
        {
            if (CoverCaching.Apply(Response, Request, "csp-" + poster.Version, string.Equals(v, poster.Version, StringComparison.Ordinal)))
            {
                await image.Stream.DisposeAsync();
                return StatusCode(StatusCodes.Status304NotModified);
            }
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(image.Stream, image.ContentType);
        }

        var first = view.Rows[collection.Members[0].Id];
        var urls = await covers.ResolveUrlsAsync([new CoverTarget(first.InternalId, first.Id, false)], ct);
        if (!urls.TryGetValue(first.InternalId, out var url))
            return NotFound(new ApiError { Error = "no_cover", Message = "This stack has no cover." });
        Response.Headers.CacheControl = CoverCaching.Revalidate;
        return Redirect(url);
    }

    private long? UserId() => long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
