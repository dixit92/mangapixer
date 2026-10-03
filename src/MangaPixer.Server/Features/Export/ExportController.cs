namespace com.lifepixer.mangapixer.Server.Features.Export;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// The read-only metadata export for MangaList (1.33.0): GET only, under <see cref="ExportApi.RoutePrefix"/>, guarded by
/// <see cref="ExportApi.Policy"/> (an admin's cookie login or a personal access token with the export scope). Built from stored data,
/// never a provider request. Bodies are written with the export's own JSON conventions (<see cref="ExportJson"/>).
/// </summary>
[ApiController]
[Route(ExportApi.RoutePrefix)]
[Authorize(Policy = ExportApi.Policy)]
public sealed class ExportController(ExportService service) : ControllerBase
{
    /// <summary>Every library with its declared kind and counts.</summary>
    [HttpGet("libraries")]
    [ProducesResponseType<ExportLibrariesDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Libraries(CancellationToken ct = default) =>
        Json(StatusCodes.Status200OK, ExportJson.Serialize(await service.LibrariesAsync(ct)));

    /// <summary>
    /// One page of a library's items (keyset paging, <paramref name="limit"/> 1-500, default 200). With <paramref name="updatedSince"/>
    /// (inclusive) only items changed since then, and on the first page the nodes removed since then; 409 <c>fullSyncRequired</c> when it
    /// lies before the removal window.
    /// </summary>
    [HttpGet("metadata")]
    [ProducesResponseType<ExportMetadataPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ExportErrorDto>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ExportErrorDto>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ExportErrorDto>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Metadata(
        [FromQuery] string? library = null, [FromQuery] string? updatedSince = null, [FromQuery] string? cursor = null,
        [FromQuery] int? limit = null, [FromQuery] string? include = null, CancellationToken ct = default)
    {
        var answer = await service.PageAsync(library, updatedSince, cursor, limit, include, ct);
        return answer.Json is { } json
            ? Json(answer.Status, json)
            : Json(answer.Status, ExportJson.Serialize(new ExportErrorDto { Error = answer.Error ?? "error" }));
    }

    private ContentResult Json(int status, string body) =>
        new() { StatusCode = status, Content = body, ContentType = "application/json; charset=utf-8" };
}
