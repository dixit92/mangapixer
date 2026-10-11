namespace com.lifepixer.mangapixer.Server.Features.Reading.Suggestions;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Reading-mode suggestions (1.41.0), admin-only: the list (local evidence only - nothing contacts a provider) and Apply, which writes
/// the folders' reader defaults like the folder reader-default PUT. The POST is checked by the global antiforgery filter.
/// </summary>
[ApiController]
[Route("api/v1/admin/reading-mode-suggestions")]
[Authorize(Policy = "Admin")]
public sealed class ReadingModeSuggestionsController : ControllerBase
{
    private readonly ReadingModeSuggestionService _suggestions;

    public ReadingModeSuggestionsController(ReadingModeSuggestionService suggestions) => _suggestions = suggestions;

    [HttpGet]
    [ProducesResponseType<ReadingModeSuggestionsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await _suggestions.ListAsync(ct));

    [HttpPost("apply")]
    [ProducesResponseType<ReadingModeApplyResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Apply([FromBody] ReadingModeApplyRequest request, CancellationToken ct)
    {
        var (error, result) = await _suggestions.ApplyAsync(request, User.Identity?.Name, ct);
        return error switch
        {
            null => Ok(result),
            "too_many_items" => BadRequest(new ApiError
            {
                Error = error,
                Message = $"At most {ReadingModeSuggestionService.MaxApplyItems} folders per request.",
            }),
            "invalid_mode" => BadRequest(new ApiError { Error = error, Message = "Unknown reader mode." }),
            "duplicate_items" => BadRequest(new ApiError { Error = error, Message = "A folder is listed more than once." }),
            _ => BadRequest(new ApiError { Error = error, Message = "List the folders to apply." }),
        };
    }
}
