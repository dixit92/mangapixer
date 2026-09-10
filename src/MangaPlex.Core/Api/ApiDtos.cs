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

    /// <summary>
    /// This folder's own global reader-mode override (1.2.0), or null if none.
    /// Only populated for folders; admins set it in the browse view.
    /// </summary>
    public ReaderMode? ReaderDefault { get; init; }

    /// <summary>
    /// Whether the current user has marked this item read (1.2.0 sticky read flag).
    /// Only meaningful for archives; folders are always false (their read-ness is
    /// managed in bulk over descendants, not stored on the folder itself).
    /// </summary>
    public bool IsRead { get; init; }
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
/// The resolved effective default reader mode for an item (1.2.0), after applying the
/// per-user item override and the global folder/library defaults.
/// </summary>
public sealed record EffectiveReaderModeDto
{
    public required ReaderMode ReaderMode { get; init; }
}

/// <summary>
/// Current user's sticky read-mark state for a single item (1.2.0).
/// </summary>
public sealed record ReadMarkDto
{
    public required string ItemId { get; init; }
    public required bool IsRead { get; init; }
}

/// <summary>
/// Per-user library browse presentation (1.2.0). Deliberately string-typed and
/// tolerant/extensible: a frontend maps values it knows and falls back gracefully
/// for any it doesn't (owner-settled multi-frontend rationale). Known values today:
/// ViewMode = grid | list | poster; Density = comfortable | compact;
/// Sort = name | recentlyAdded | recentlyRead.
/// </summary>
public sealed record LibraryViewPreferencesDto
{
    public string ViewMode { get; init; } = "grid";
    public string Density { get; init; } = "comfortable";
    public string Sort { get; init; } = "name";
}

/// <summary>
/// The current user's Private library designations (1.4.0). Libraries in this
/// list are hidden from listing/discovery surfaces (continue-reading, search,
/// browse-root, library list) while Incognito mode is active. Direct reader
/// URLs remain accessible regardless. Library IDs are opaque public IDs.
/// </summary>
public sealed record PrivateLibrariesDto
{
    public required IReadOnlyList<string> LibraryIds { get; init; }
}

/// <summary>
/// Request to replace the current user's Private library set (1.4.0). The
/// entire list is replaced on each call. Unknown library IDs are silently
/// skipped.
/// </summary>
public sealed record SetPrivateLibrariesRequest
{
    public required IReadOnlyList<string> LibraryIds { get; init; }
}

/// <summary>
/// Result of a bulk read-mark operation over a folder's descendant archives (1.2.0).
/// </summary>
public sealed record BulkReadMarkResultDto
{
    /// <summary>Number of descendant archives affected (newly set, or cleared).</summary>
    public required int Affected { get; init; }

    /// <summary>Total descendant archives considered under the folder.</summary>
    public required int Total { get; init; }
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

    /// <summary>
    /// Global default reader mode for the library (1.2.0), or null to inherit the
    /// user's personal default. Admin-set; applies to all users, overridable per
    /// folder.
    /// </summary>
    public ReaderMode? DefaultReaderMode { get; init; }
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
/// Request to set a global default reader mode on a library or a folder (1.2.0).
/// Clearing (inherit) is a DELETE, not this request.
/// </summary>
public sealed record SetReaderModeRequest
{
    public required ReaderMode ReaderMode { get; init; }
}

/// <summary>
/// Response when a scan is triggered.
/// </summary>
public sealed record ScanTriggeredDto
{
    public required string ScanRunId { get; init; }
}

/// <summary>
/// Response when durable thumbnail regeneration is enqueued for a library.
/// Generation runs in the background; <see cref="QueuedCount"/> is the number
/// of items that lacked a current thumbnail.
/// </summary>
public sealed record ThumbnailRegenerateResponse
{
    public required int QueuedCount { get; init; }
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

// --- YACReader progress import (admin-only) ---
//
// A read-only importer that maps reading progress from a YACReader
// .yacreaderlibrary/library.ydb SQLite database into MangaPlex reading
// progress + sticky read-marks for a chosen target user. Covers and
// thumbnails are not imported (MangaPlex regenerates them). Source media is
// never mutated: the ydb is opened read-only, or copied to an app-owned
// scratch snapshot first (default). Existing MangaPlex state is never
// overwritten unless the admin explicitly opts in via Overwrite.

/// <summary>
/// Request for a YACReader progress import (preview or apply). The admin
/// selects a MangaPlex library to map into, the path to the YACReader
/// library database (the .yacreaderlibrary directory or the library.ydb
/// file), and the target MangaPlex user to receive the imported progress.
/// </summary>
public sealed record YacReaderImportRequest
{
    /// <summary>Opaque public id of the MangaPlex library to map into.</summary>
    public required string LibraryId { get; init; }

    /// <summary>
    /// Optional explicit path to the YACReader library database (the
    /// .yacreaderlibrary directory or the library.ydb file). A private server
    /// locator, never echoed in responses. When omitted, the server auto-detects
    /// <c>.yacreaderlibrary/library.ydb</c> inside the mapped library's own root —
    /// the normal case, so the client never handles a path.
    /// </summary>
    public string? YacDbPath { get; init; }

    /// <summary>Opaque public id of the MangaPlex user to import progress for.</summary>
    public required string TargetUserId { get; init; }

    /// <summary>
    /// When false (default), items that already have MangaPlex progress are
    /// skipped (never overwrite existing state without explicit opt-in).
    /// When true, existing progress and read-marks are replaced.
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// When true (default), library.ydb is copied to an app-owned scratch
    /// snapshot before reading, so the source library directory is never
    /// opened for write (no journal/wal/shm sidecar creation). When false,
    /// the source ydb is opened read-only directly.
    /// </summary>
    public bool Snapshot { get; init; } = true;
}

/// <summary>
/// Result of detecting a YACReader library inside a MangaPlex library's root.
/// The source path is resolved server-side and never returned.
/// </summary>
public sealed record YacReaderDetectDto
{
    /// <summary>True when a readable <c>library.ydb</c> was found for the library.</summary>
    public required bool Detected { get; init; }

    /// <summary>YACReader db schema version (from <c>db_info</c>), when detected and readable.</summary>
    public string? DbVersion { get; init; }
}

/// <summary>
/// One mapped item in a YACReader import preview. Source paths are never
/// exposed; only the mapped MangaPlex item id/display name and the imported
/// reading state are surfaced.
/// </summary>
public sealed record YacReaderImportItemDto
{
    /// <summary>Opaque public id of the mapped MangaPlex archive item, or null if unmapped.</summary>
    public string? ItemId { get; init; }

    /// <summary>Display name of the mapped MangaPlex item, or null if unmapped.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Whether YACReader marked the comic as read (finished).</summary>
    public required bool Read { get; init; }

    /// <summary>Whether YACReader recorded the comic as having been opened.</summary>
    public required bool HasBeenOpened { get; init; }

    /// <summary>YACReader's 1-based current page (0 if unknown).</summary>
    public required int CurrentPage { get; init; }

    /// <summary>Resolved MangaPlex reading state: "unread", "inProgress", or "completed".</summary>
    public required string State { get; init; }

    /// <summary>True if MangaPlex already has progress for this item (a conflict).</summary>
    public required bool Conflict { get; init; }
}

/// <summary>
/// Preview (dry-run) of a YACReader progress import. No state is written.
/// </summary>
public sealed record YacReaderImportPreviewDto
{
    public required string LibraryId { get; init; }
    public required string TargetUserId { get; init; }

    /// <summary>YACReader database schema version (db_info.version), if available.</summary>
    public string? DbVersion { get; init; }

    public required int TotalComics { get; init; }
    public required int Mapped { get; init; }
    public required int Unmapped { get; init; }

    /// <summary>Mapped items that already have MangaPlex progress (conflicts).</summary>
    public required int Conflicts { get; init; }

    /// <summary>Items that would be imported (mapped and not skipped by conflict policy).</summary>
    public required int ToImport { get; init; }

    /// <summary>Bounded sample of mapped items (at most 50), for review.</summary>
    public required IReadOnlyList<YacReaderImportItemDto> Items { get; init; }
}

/// <summary>
/// Result of applying a YACReader progress import.
/// </summary>
public sealed record YacReaderImportResultDto
{
    public required string LibraryId { get; init; }
    public required string TargetUserId { get; init; }
    public string? DbVersion { get; init; }
    public required int TotalComics { get; init; }
    public required int Mapped { get; init; }
    public required int Unmapped { get; init; }

    /// <summary>Progress rows written or updated.</summary>
    public required int Imported { get; init; }

    /// <summary>Mapped items skipped because MangaPlex already had progress (overwrite=false).</summary>
    public required int Skipped { get; init; }

    /// <summary>Sticky read-marks set (for comics YACReader marked read).</summary>
    public required int ReadMarks { get; init; }
}
