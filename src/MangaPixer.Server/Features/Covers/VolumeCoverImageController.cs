namespace com.lifepixer.mangapixer.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Stored web covers for ADMINS (1.29.0): the picker's image of one stored cover (by its public id - everyone else sees a
/// web cover only through a node's cover URL) and "Delete stored volume covers" (the privacy-conscious undo: files, rows,
/// the automatic decisions that used them and the admin choices that pointed at them - those nodes fall back to automatic).
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = "Admin")]
public sealed class VolumeCoverImageController(MangaPixerDbContext db, CoverFiles files, AuditService audit,
    ILogger<VolumeCoverImageController> logger) : ControllerBase
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

    [HttpDelete("admin/metadata/volume-covers")]
    [ProducesResponseType<DeleteVolumeCoversResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteAll(CancellationToken ct)
    {
        var decisions = await db.NodeAutoCovers
            .Where(a => a.VolumeCoverId != null
                || a.Source == (int)AutoCoverSource.WebVolume || a.Source == (int)AutoCoverSource.WebMain)
            .ExecuteDeleteAsync(ct);
        var choices = await db.NodeCoverChoices.Where(c => c.Mode == (int)CoverChoiceMode.VolumeCover).ExecuteDeleteAsync(ct);
        var covers = await db.VolumeCovers.ExecuteDeleteAsync(ct);
        try
        {
            if (Directory.Exists(files.VolumeCoversRoot))
                Directory.Delete(files.VolumeCoversRoot, recursive: true);
        }
        catch (IOException) { /* best effort: rows are gone, orphan files are never served */ }
        catch (UnauthorizedAccessException) { /* best effort */ }

        await audit.RecordAsync(AuditActions.VolumeCoversDelete, AuditResults.Success, User.Identity?.Name, ct: ct);
        logger.LogInformation(LogEvents.Metadata.VolumeCoversDeleted, "Stored volume covers deleted ({Covers} covers, {Decisions} decisions, {Choices} choices)",
            covers, decisions, choices);
        return Ok(new DeleteVolumeCoversResult { CoversDeleted = covers, DecisionsReset = decisions, ChoicesReset = choices });
    }
}
