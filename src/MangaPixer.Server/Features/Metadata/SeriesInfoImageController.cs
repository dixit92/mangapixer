namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The linked series poster for a node (1.24.0, lane B2), served from MangaPixer's
/// own store - the browser never contacts a provider. Reachable only THROUGH a
/// node, with the node's access rules: no access, no link, hidden series info or
/// no stored image all answer 404 (never 403), so a record's image is never
/// reachable by record id and a non-member learns nothing.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class SeriesInfoImageController : ControllerBase
{
    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _libraryAuth;
    private readonly SeriesInfoResolver _resolver;
    private readonly MetadataSettingsService _settings;
    private readonly MetadataImageStore _images;

    public SeriesInfoImageController(
        MangaPixerDbContext db,
        LibraryAuthorizationService libraryAuth,
        SeriesInfoResolver resolver,
        MetadataSettingsService settings,
        MetadataImageStore images)
    {
        _db = db;
        _libraryAuth = libraryAuth;
        _resolver = resolver;
        _settings = settings;
        _images = images;
    }

    /// <summary>
    /// The poster image. The <c>v</c> query value (record + image version, from
    /// <c>SeriesInfoDto.web.imageUrl</c>) only busts caches: the URL changes whenever
    /// the image does, so the response is cacheable forever.
    /// </summary>
    [HttpGet("nodes/{nodeId}/series-info/image")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetImage(string nodeId, [FromQuery] string? v, CancellationToken ct)
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var userId))
            return Unauthorized();

        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodeId, ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return NotFound();
        if (!await _libraryAuth.CanAccessLibraryAsync(userId, node.LibraryId, ct))
            return NotFound();
        if (await _settings.IsSeriesInfoHiddenAsync(node.LibraryId, ct))
            return NotFound();

        var record = await _resolver.ResolveWebRecordAsync(node, ct);
        if (record is not { ImageState: 1 } || _images.Open(record.Id, record.ImageVersion) is not { } image)
            return NotFound();

        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(image.Stream, image.ContentType);
    }
}
