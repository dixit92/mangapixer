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
    /// Server-side revision number for optimistic concurrency.
    /// Clients must send this as the ETag/If-Match value on subsequent
    /// PUT updates. Starts at 0 for a new (unread) item.
    /// </summary>
    public required long Revision { get; init; }

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

    /// <summary>
    /// Client-generated unique mutation ID (ULID or GUID string) for
    /// idempotent updates. A duplicate mutation ID is a no-op that
    /// returns the current revision without incrementing it.
    /// </summary>
    public required string MutationId { get; init; }

    /// <summary>
    /// Optional entry key for the current page (manifest page key).
    /// Used to verify the client is on the correct page in the manifest.
    /// </summary>
    public string? EntryKey { get; init; }

    /// <summary>
    /// Normalized scroll/position anchor (0.0–1.0) for webtoon mode.
    /// </summary>
    public double NormalizedAnchor { get; init; }
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

    /// <summary>
    /// Theme preference: "dark", "light", or "system" (audit defect D23).
    /// </summary>
    public string Theme { get; init; } = "dark";
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

    /// <summary>
    /// True when the account must change its password before it can use the app
    /// (admin-created accounts and admin password resets). While true, the server
    /// rejects every non-auth request, so the client must route to the
    /// change-password screen. Defaults false so existing sessions are unaffected.
    /// </summary>
    public bool ForcePasswordChange { get; init; }
}

/// <summary>
/// API DTO for CSRF token.
/// </summary>
public sealed record CsrfTokenDto
{
    public required string Token { get; init; }
}

/// <summary>
/// Whether the instance still needs first-run setup (no users exist yet).
/// </summary>
public sealed record SetupStatusDto
{
    public required bool SetupRequired { get; init; }
}

/// <summary>
/// First-run setup request: the admin username and password chosen by the user.
/// Accepted only while no user exists (audit finding F2 — no default credential).
/// </summary>
public sealed record SetupRequest
{
    public required string Username { get; init; }
    public required string Password { get; init; }
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

/// <summary>
/// One browsable directory entry under the media browse root. Admin-only.
/// </summary>
public sealed record DirectoryEntryDto
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool HasChildren { get; init; }
}

/// <summary>
/// A directory listing confined to the configured media browse root, used by the
/// admin library-registration path picker. When <see cref="Available"/> is false
/// no browse root is configured/accessible and the admin must type a path.
/// Absolute paths are returned only to admins and only within the browse root.
/// </summary>
public sealed record DirectoryListingDto
{
    public required bool Available { get; init; }
    public string? Root { get; init; }
    public string? Current { get; init; }
    public string? Parent { get; init; }
    public IReadOnlyList<DirectoryEntryDto> Entries { get; init; } = [];
}

// --- Admin DTOs ---

/// <summary>
/// Request to register a new library. The root path is server-side
/// (the container mount path), never a client-side path.
/// </summary>
public sealed record RegisterLibraryRequest
{
    public required string DisplayName { get; init; }
    public required string RootPath { get; init; }
}

/// <summary>
/// Request to update a library's display name.
/// </summary>
public sealed record UpdateLibraryRequest
{
    public required string DisplayName { get; init; }
}

/// <summary>
/// Response when a scan is triggered.
/// </summary>
public sealed record ScanTriggeredDto
{
    public required string ScanRunId { get; init; }
}

/// <summary>
/// DTO for a scan run status.
/// </summary>
public sealed record ScanRunDto
{
    public required string Id { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int? NodesObserved { get; init; }
    public int? NodesAdded { get; init; }
    public int? NodesTombstoned { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// DTO for a user in admin views. Never includes password hashes.
/// </summary>
public sealed record AdminUserDto
{
    public required string Id { get; init; }
    public required string Username { get; init; }
    public required bool IsAdmin { get; init; }
    public required bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastLoginAt { get; init; }
}

/// <summary>
/// Request to create a new user.
/// </summary>
public sealed record CreateUserRequest
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    public bool IsAdmin { get; init; }
}

/// <summary>
/// Request to update a user (enable/disable, admin flag).
/// </summary>
public sealed record UpdateUserRequest
{
    public bool? IsActive { get; init; }
    public bool? IsAdmin { get; init; }
}

/// <summary>
/// Response from a password reset. The temporary password is returned once
/// and never stored in plaintext.
/// </summary>
public sealed record ResetPasswordResponse
{
    public required string TemporaryPassword { get; init; }
}

/// <summary>
/// Request to grant or revoke library access.
/// </summary>
public sealed record LibraryGrantDto
{
    public required string UserId { get; init; }
    public required string LibraryId { get; init; }
}

/// <summary>
/// The set of libraries a user is currently granted access to, by public id.
/// Admins implicitly access all libraries; for an admin this list still reflects
/// only explicit grant rows (usually empty), with <see cref="IsAdmin"/> signalling
/// that access is unrestricted regardless.
/// </summary>
public sealed record UserGrantsDto
{
    public required string UserId { get; init; }
    public required bool IsAdmin { get; init; }
    public required IReadOnlyList<string> LibraryIds { get; init; }
}
