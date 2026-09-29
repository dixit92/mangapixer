namespace com.lifepixer.mangapixer.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The cover of a virtual volume stack (1.29.0): <c>GET /nodes/{folderId}/volumes/{key}/cover?v=</c>, for anyone who can see
/// the folder's library (a reader never needs the admin-only <c>/volume-covers/{id}/image</c>). A chapter-only stack with a
/// stored web cover of its volume (and the web-cover switches on) gets that image - the current version cached a year,
/// <c>private</c>, any other version revalidates. Otherwise (a real volume archive, no stored cover, the switches off since
/// the URL was built) the request is redirected, uncached, to the stack's first member's current cover.
/// </summary>
[ApiController]
[Route("api/v1/nodes/{nodeId}/volumes/{key}/cover")]
[Authorize]
public sealed class StackCoverController(
    MangaPixerDbContext db,
    LibraryAuthorizationService libraryAuth,
    VolumeEntryService entries,
    StackCoverService stackCovers,
    ICoverResolver covers,
    CoverFiles files) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> Get(string nodeId, string key, [FromQuery] string? v, CancellationToken ct)
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var userId))
            return Unauthorized();
        var folder = await db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodeId, ct);
        if (folder is null || folder.Kind != (int)CatalogNodeKind.Folder || folder.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return NotFound();
        if (!await libraryAuth.CanAccessLibraryAsync(userId, folder.LibraryId, ct))
            return NotFound();

        var view = await entries.GetEntriesAsync(folder.Id, ct);
        var stack = view?.Entries.FirstOrDefault(e => e.Kind == VolumeEntryKind.Stack && string.Equals(e.Stack!.Key, key, StringComparison.Ordinal))?.Stack;
        if (view is null || stack is null)
            return NotFound();

        var web = await stackCovers.ResolveAsync(view.FolderId, view.FolderPublicId, view.LibraryId, [stack], ct);
        if (web.TryGetValue(stack.Key, out var cover) && CoverFiles.OpenRead(files.VolumeCoverPath(cover.VolumeCoverPublicId, cover.StoredVersion)) is { } stream)
        {
            if (CoverCaching.Apply(Response, Request, "stk-" + cover.Version, string.Equals(v, cover.Version, StringComparison.Ordinal)))
            {
                await stream.DisposeAsync();
                return StatusCode(StatusCodes.Status304NotModified);
            }
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(stream, "image/webp");
        }

        // No web cover applies: the first member's cover (its own versioned URL decides its caching).
        var first = view.Rows[stack.Members[0].Row.Id];
        var urls = await covers.ResolveUrlsAsync([new CoverTarget(first.InternalId, first.Id, false)], ct);
        if (!urls.TryGetValue(first.InternalId, out var url))
            return NotFound(new ApiError { Error = "no_cover", Message = "This volume has no cover." });
        Response.Headers.CacheControl = CoverCaching.Revalidate;
        return Redirect(url);
    }
}
