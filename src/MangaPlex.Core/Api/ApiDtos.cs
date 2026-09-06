namespace com.lifepixer.mangaplex.Core.Api;

using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Core.Reading;

/// <summary>
/// Pagination cursor for keyset pagination. Encoded as an opaque string.
/// The cursor contains the sort key of the last item on the current page.
/// </summary>
public sealed record PageCursor
{
    /// <summary>
    /// Opaque cursor string. Null or empty means "first page".
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>
    /// Page size requested.
    /// </summary>
    public int PageSize { get; init; } = 50;

    /// <summary>
    /// Sort direction.
    /// </summary>
    public SortDirection Direction { get; init; } = SortDirection.Ascending;
}

/// <summary>
/// Sort direction for paginated queries.
/// </summary>
public enum SortDirection
{
    Ascending = 0,
    Descending = 1
}

/// <summary>
/// A paginated response page.
/// </summary>
public sealed record PageResponse<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int TotalCount { get; init; }
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>
/// API DTO for a catalog node (folder or archive).
/// Contains no source paths.
/// </summary>
public sealed record CatalogNodeDto
{
    public required string Id { get; init; }
    public required string ParentId { get; init; }
    public required string LibraryId { get; init; }
    public required CatalogNodeKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required CatalogNodeAvailability Availability { get; init; }

    /// <summary>
    /// Cover thumbnail URL (opaque), if available.
    /// </summary>
    public string? CoverUrl { get; init; }

    /// <summary>
    /// Number of child folders, if this is a folder.
    /// </summary>
    public int? ChildFolderCount { get; init; }

    /// <summary>
    /// Number of child archives, if this is a folder.
    /// </summary>
    public int? ChildArchiveCount { get; init; }

    /// <summary>
    /// Page count, if this is an archive with a manifest.
    /// </summary>
    public int? PageCount { get; init; }

    /// <summary>
    /// Reading state for the current user, if authenticated.
    /// </summary>
    public ReadingState? ReadingState { get; init; }

    /// <summary>
    /// Last read page index for the current user, if any.
    /// </summary>
    public int? LastReadPage { get; init; }
}

/// <summary>
/// API DTO for breadcrumbs (path from library root to a node).
/// </summary>
public sealed record BreadcrumbsDto
{
    public required string NodeId { get; init; }
    public required IReadOnlyList<BreadcrumbEntry> Trail { get; init; }
}

public sealed record BreadcrumbEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
}

/// <summary>
/// API DTO for search results.
/// </summary>
public sealed record SearchResultsDto
{
    public required string Query { get; init; }
    public required IReadOnlyList<CatalogNodeDto> Items { get; init; }
    public required int TotalCount { get; init; }
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>
/// API DTO for reading progress.
/// </summary>
public sealed record ReadingProgressDto
{
    public required string ItemId { get; init; }
    public required int PageIndex { get; init; }
    public required long ContentVersion { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required ReadingState State { get; init; }

    /// <summary>
    /// Content version the progress was recorded against.
    /// If the current content version differs, the progress is stale.
    /// </summary>
    public bool IsStale { get; init; }
}

/// <summary>
/// API DTO for updating reading progress.
/// </summary>
public sealed record UpdateProgressRequest
{
    public required int PageIndex { get; init; }

    /// <summary>
    /// Content version the client believes the item is at.
    /// If this doesn't match the current version, the update is rejected.
    /// </summary>
    public required long ExpectedContentVersion { get; init; }
}

/// <summary>
/// API DTO for user preferences.
/// </summary>
public sealed record UserPreferencesDto
{
    public ReaderMode DefaultReaderMode { get; init; } = ReaderMode.PagedLtr;
    public bool PreferDoubleSpread { get; init; } = false;
    public bool ReducedMotion { get; init; } = false;
    public string? PreferredBackground { get; init; }
}

/// <summary>
/// API DTO for a library.
/// </summary>
public sealed record LibraryDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// Whether the library is currently being scanned.
    /// </summary>
    public bool IsScanning { get; init; }

    /// <summary>
    /// Total item count, if known.
    /// </summary>
    public int? ItemCount { get; init; }

    /// <summary>
    /// Last scan completion time, if any.
    /// </summary>
    public DateTimeOffset? LastScanCompleted { get; init; }
}

/// <summary>
/// Standard API error response.
/// </summary>
public sealed record ApiError
{
    public required string Error { get; init; }
    public required string Message { get; init; }
    public string? Detail { get; init; }
    public string? CorrelationId { get; init; }
}

/// <summary>
/// API DTO for the authenticated user.
/// </summary>
public sealed record AuthUserDto
{
    public required string Id { get; init; }
    public required string Username { get; init; }
    public required string Role { get; init; }
    public required bool IsAdmin { get; init; }
}

/// <summary>
/// API DTO for CSRF token.
/// </summary>
public sealed record CsrfTokenDto
{
    public required string Token { get; init; }
}

/// <summary>
/// API DTO for login request.
/// </summary>
public sealed record LoginRequest
{
    public required string Username { get; init; }
    public required string Password { get; init; }
}

/// <summary>
/// API DTO for password change request.
/// </summary>
public sealed record ChangePasswordRequest
{
    public required string CurrentPassword { get; init; }
    public required string NewPassword { get; init; }
}
