namespace com.lifepixer.mangapixer.Server.Features.Metadata.Declared;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Admin-only declared facts (1.28.0): read / replace / clear what is declared on a folder or a library.
/// PUT replaces the scope's type and creator list (a null type or an empty list clears that key); DELETE
/// clears both. Audited with ids only. No network.
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata")]
[Authorize(Policy = "Admin")]
public sealed class DeclaredFactsAdminController : ControllerBase
{
    private readonly DeclaredFactsService _facts;

    public DeclaredFactsAdminController(DeclaredFactsService facts) => _facts = facts;

    private string? Actor => User.Identity?.Name;

    [HttpGet("folders/{nodeId}/declared")]
    [ProducesResponseType<DeclaredFactsScopeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFolder(string nodeId, CancellationToken ct) =>
        ToResult(await _facts.GetFolderAsync(nodeId, ct));

    [HttpPut("folders/{nodeId}/declared")]
    [ProducesResponseType<DeclaredFactsScopeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetFolder(string nodeId, [FromBody] SetDeclaredFactsRequest request, CancellationToken ct) =>
        ToResult(await _facts.SetFolderAsync(nodeId, request, Actor, ct));

    [HttpDelete("folders/{nodeId}/declared")]
    [ProducesResponseType<DeclaredFactsScopeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearFolder(string nodeId, CancellationToken ct) =>
        ToResult(await _facts.ClearFolderAsync(nodeId, Actor, ct));

    [HttpGet("libraries/{libraryId}/declared")]
    [ProducesResponseType<DeclaredFactsScopeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLibrary(string libraryId, CancellationToken ct) =>
        ToResult(await _facts.GetLibraryAsync(libraryId, ct));

    [HttpPut("libraries/{libraryId}/declared")]
    [ProducesResponseType<DeclaredFactsScopeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetLibrary(string libraryId, [FromBody] SetDeclaredFactsRequest request, CancellationToken ct) =>
        ToResult(await _facts.SetLibraryAsync(libraryId, request, Actor, ct));

    [HttpDelete("libraries/{libraryId}/declared")]
    [ProducesResponseType<DeclaredFactsScopeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearLibrary(string libraryId, CancellationToken ct) =>
        ToResult(await _facts.ClearLibraryAsync(libraryId, Actor, ct));

    private IActionResult ToResult(DeclaredFactsResult<DeclaredFactsScopeDto> result) => result.Code switch
    {
        MetadataLinkResultCode.Ok => Ok(result.Value),
        MetadataLinkResultCode.NodeNotFound or MetadataLinkResultCode.LibraryNotFound => NotFound(),
        MetadataLinkResultCode.NotAFolder => BadRequest(new ApiError
        {
            Error = "not_a_folder",
            Message = "Declared facts can only be set on a folder or a library.",
        }),
        _ => BadRequest(new ApiError { Error = result.Error ?? "invalid_request", Message = MessageFor(result.Error) }),
    };

    private static string MessageFor(string? error) => error switch
    {
        "type_invalid" => "Unknown type.",
        "creators_too_many" => $"At most {Core.Metadata.DeclaredFactKeys.MaxCreators} creators.",
        "creator_name_invalid" => $"A creator name must be 1-{Core.Metadata.DeclaredFactKeys.MaxValueLength} characters.",
        "creator_role_invalid" => "A creator role must be author, writer or artist (or none).",
        _ => "The request is not valid.",
    };
}

/// <summary>
/// Declared facts for the Info panel and the series page (1.28.0). Follows the node's access rules exactly
/// like series information: no access to the node's library answers 404 (never 403), so a non-member cannot
/// learn that the node exists; direct access works in Incognito.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class NodeDeclaredFactsController : ControllerBase
{
    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _libraryAuth;
    private readonly DeclaredFactsService _facts;

    public NodeDeclaredFactsController(MangaPixerDbContext db, LibraryAuthorizationService libraryAuth, DeclaredFactsService facts)
    {
        _db = db;
        _libraryAuth = libraryAuth;
        _facts = facts;
    }

    [HttpGet("nodes/{nodeId}/declared-facts")]
    [ProducesResponseType<NodeDeclaredFactsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string nodeId, CancellationToken ct)
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var userId))
            return Unauthorized();

        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodeId, ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return NotFound();
        if (!await _libraryAuth.CanAccessLibraryAsync(userId, node.LibraryId, ct))
            return NotFound();

        return Ok(await _facts.ForNodeAsync(node, ct));
    }
}
