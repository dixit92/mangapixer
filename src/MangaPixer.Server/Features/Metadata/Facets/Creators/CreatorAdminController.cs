namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Creators;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Admin creator API (1.45.0 lane F, D1 2.2): look up, search, merge, split and rename canonical creators. Admin policy, cookie + CSRF (the
/// app's antiforgery filter); merge / split audited with public ids only. A merged creator's id answers with its target (200 +
/// <c>redirectedFrom</c>). No network.
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata/creators")]
[Authorize(Policy = "Admin")]
public sealed class CreatorAdminController : ControllerBase
{
    private readonly CreatorAdminService _creators;

    public CreatorAdminController(CreatorAdminService creators) => _creators = creators;

    private string? Actor => User.Identity?.Name;

    /// <summary>Live creators whose spellings contain <paramref name="q"/> (by name key), at most <paramref name="limit"/> (1-50).</summary>
    [HttpGet]
    [ProducesResponseType<CreatorSearchResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int limit = 20, CancellationToken ct = default) =>
        Ok(await _creators.SearchAsync(q, limit, ct));

    [HttpGet("{id}")]
    [ProducesResponseType<CreatorDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct) =>
        await _creators.GetAsync(id, ct) is { } creator ? Ok(creator) : NotFoundError();

    /// <summary>Merges the creator into <c>intoId</c>; answers with the target.</summary>
    [HttpPost("{id}/merge")]
    [ProducesResponseType<CreatorDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Merge(string id, [FromBody] MergeCreatorRequest request, CancellationToken ct) =>
        Answer(await _creators.MergeAsync(id, request.IntoId, Actor, ct));

    /// <summary>Splits the chosen provider ids and spellings into a new creator; answers with the new creator.</summary>
    [HttpPost("{id}/split")]
    [ProducesResponseType<CreatorDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Split(string id, [FromBody] SplitCreatorRequest request, CancellationToken ct) =>
        Answer(await _creators.SplitAsync(id, request.ProviderIds, request.Names, Actor, ct));

    /// <summary>Sets the admin display name (blank clears it).</summary>
    [HttpPut("{id}/display-name")]
    [ProducesResponseType<CreatorDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDisplayName(string id, [FromBody] SetCreatorDisplayNameRequest request, CancellationToken ct) =>
        Answer(await _creators.SetDisplayNameAsync(id, request.DisplayName, Actor, ct));

    private IActionResult Answer(CreatorAdminResult result) => result switch
    {
        { Creator: { } creator } => Ok(creator),
        { Error: "not_found" } => NotFoundError(),
        { Error: { } error } => BadRequest(new ApiError { Error = error, Message = Message(error) }),
        _ => NotFoundError(),
    };

    private NotFoundObjectResult NotFoundError() => NotFound(new ApiError { Error = "not_found", Message = "No such creator." });

    private static string Message(string error) => error switch
    {
        "same_creator" => "A creator cannot be merged into itself.",
        "empty_selection" => "Choose at least one provider id or spelling to split off.",
        "not_on_creator" => "Every chosen provider id and spelling must belong to this creator.",
        "split_all" => "A split must leave the creator at least one provider id or spelling.",
        _ => "The request is not valid.",
    };
}
