namespace com.lifepixer.mangapixer.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Stored web covers for ADMINS (1.29.0): the picker's image of one stored cover (by its public id - everyone else sees a
/// web cover only through a node's cover URL). "Delete stored volume covers" is `DELETE /admin/metadata/volume-covers`
/// (<see cref="Metadata.Volumes.VolumeCoversController"/>).
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = "Admin")]
public sealed class VolumeCoverImageController(MangaPixerDbContext db, CoverFiles files) : ControllerBase
{
    [HttpGet("volume-covers/{coverId}/image")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetImage(string coverId, [FromQuery] string? v, CancellationToken ct)
    {
        var cover = await db.VolumeCovers.AsNoTracking().FirstOrDefaultAsync(c => c.PublicId == coverId, ct);
        if (cover is null || cover.StoredVersion <= 0 || cover.State is not ((int)VolumeCoverState.Stored or (int)VolumeCoverState.Gone))
            return NotFound();
        if (CoverFiles.OpenRead(files.VolumeCoverPath(cover.PublicId, cover.StoredVersion)) is not { } stream)
            return NotFound();
        var current = string.Equals(v, cover.StoredVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (CoverCaching.Apply(Response, Request, $"vc-{cover.PublicId}-{cover.StoredVersion}", current))
        {
            await stream.DisposeAsync();
            return StatusCode(StatusCodes.Status304NotModified);
        }
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(stream, "image/webp");
    }
}
