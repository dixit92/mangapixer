namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;

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

    /// <summary>
    /// Backward (upward) keyset cursor (1.11.0): the opaque token to pass as the
    /// browse <c>before</c> param to fetch the page immediately BEFORE this window's
    /// first item. Null when this window starts at the true first item of the listing
    /// (nothing precedes it) or when the sort does not support backward paging (only
    /// the name sort does today - the jump rail is name-sort only). Additive; older
    /// clients ignore it. Enables upward infinite-scroll after a mid-list jump, where
    /// the forward-only cursor previously stranded the user (1.8.0 finding).
    /// </summary>
    public string? PrevCursor { get; init; }

    /// <summary>
    /// Whether a page exists BEFORE this window's first item (1.11.0). Pairs with
    /// <see cref="PrevCursor"/>: true means the client may scroll up and prepend the
    /// previous page. Always false for a window loaded from the listing start and for
    /// sorts without backward paging. Additive.
    /// </summary>
    public bool HasPrevious { get; init; }

    /// <summary>
    /// The folder's next-to-read descendant archive (1.7.0), surfaced as a pinned
    /// "Continue" row above the sorted list. Only populated by catalog browse; null
    /// for every other page response and when the browsed folder has no unread
    /// descendant archive. Resolution: the in-progress archive if one exists (resume
    /// the most recently updated), else the first UNREAD archive in SortKey order
    /// (ordinal, matching NaturalOrderComparer); null when every descendant is read.
    /// Additive - older clients that ignore it are unaffected. Never stored.
    /// </summary>
    public CatalogNodeDto? NextUnread { get; init; }
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

    /// <summary>
    /// Whether the current user has starred this node as a favorite (1.21.0). Applies
    /// uniformly to folders and archives (favorites are per-node, no rollup). Populated
    /// by browse, search, single-node lookup, and the favorites list via a single
    /// batched join (no N+1); defaults false so older clients ignore it.
    /// </summary>
    public bool IsFavorite { get; init; }

    /// <summary>
    /// Derived read rollup over this folder's readable descendant archives for the
    /// current user (1.6.0): Read when every one carries a read-mark, Reading when
    /// some are read or in progress, Unread when none are. Only populated in browse
    /// and only for folders that have at least one readable descendant archive; null
    /// for archives, empty folders, and search results. Never stored - computed from
    /// the same per-item signals as <see cref="IsRead"/> / <see cref="ReadingState"/>.
    /// </summary>
    public FolderReadRollup? ReadRollup { get; init; }
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
    /// The page index the reader should OPEN at for this item (1.9.0), computed
    /// per-archive at read time and NON-DESTRUCTIVELY (the stored <see cref="PageIndex"/>
    /// is never rewritten because of this). For an item WITHOUT a read-mark this equals
    /// <see cref="PageIndex"/> (resume where you left off). For a READ item (has a
    /// read-mark): the last page (PageIndex &gt;= PageCount-1) always opens at 0; a
    /// mid-archive position opens at 0 when the user's
    /// <see cref="UserPreferencesDto.AlwaysOpenReadFromStart"/> is on, else resumes; no
    /// saved position opens at 0. Keys off POSITION, not the Completed enum, so it is
    /// robust to the re-read State-flip. Additive — older clients ignore it and keep
    /// using <see cref="PageIndex"/>.
    /// </summary>
    public int OpenPageIndex { get; init; }

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
/// Request body for <c>PUT /api/v1/items/{itemId}/spread-layout</c> (1.23.0): replace the
/// archive's shared double-page pairing. <see cref="SpreadStarts"/> must be sorted
/// ascending, unique, each within [1, PageCount-1], and at most PageCount entries; an
/// empty list is a valid explicit "no shifts" (overrides the reader's device fallback).
/// </summary>
public sealed record SetSpreadLayoutRequest
{
    /// <summary>
    /// Content version the client rendered the pairing against (the manifest's).
    /// A mismatch is rejected with 409 <c>stale_content</c>, like progress updates.
    /// </summary>
    public required long ExpectedContentVersion { get; init; }

    /// <summary>Forced spread-start page indices (zero-based).</summary>
    public required IReadOnlyList<int> SpreadStarts { get; init; }
}

/// <summary>
/// The saved double-page pairing for one archive (1.23.0), as returned by the
/// spread-layout write. Readers receive the same data as <c>ItemManifest.SpreadStarts</c>.
/// </summary>
public sealed record SpreadLayoutDto
{
    public required string ItemId { get; init; }
    public required long ContentVersion { get; init; }
    public required IReadOnlyList<int> SpreadStarts { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
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

    /// <summary>
    /// When on, archives the user has marked read reopen from the FIRST page (1.9.0).
    /// When off (default), read titles reopen where the user left off; titles they
    /// finished (saved position on the last page) still start from the first page.
    /// Only affects archives that carry a read-mark — Unread/Reading archives always
    /// resume. Evaluated non-destructively at open time (see
    /// <see cref="ReadingProgressDto.OpenPageIndex"/>), so toggling it is instant and
    /// fully reversible.
    /// </summary>
    public bool AlwaysOpenReadFromStart { get; init; } = false;
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
/// ViewMode = card | list (legacy grid | poster are tolerated and read as card);
/// Density = comfortable | compact (legacy, subsumed by CardSize);
/// Sort = name | recentlyAdded | recentlyRead; Direction = "" | asc | desc;
/// CardSize = "" | a stringified min column width in px (e.g. "150").
///
/// Direction (1.5.0) defaults to "" (unset) rather than baking in a fixed default,
/// because the sensible default differs per sort (Name ascending; recentlyAdded/
/// recentlyRead descending). Consumers resolve "" to the sort-specific default —
/// see <c>CatalogController.ParseDirection</c> — so existing stored preferences
/// (saved before this field existed) keep their pre-1.5.0 ordering unchanged.
///
/// CardSize (1.6.0) merges the old Grid and Poster modes into one Card view whose
/// size is a continuous slider that subsumes the comfortable/compact Density split.
/// It defaults to "" (unset) for the same graceful-migration reason as Direction:
/// a frontend derives an initial size from the legacy ViewMode + Density, so rows
/// saved before this field existed keep their effective card size. The server never
/// interprets these presentation strings — they are stored and returned verbatim.
///
/// LibraryPageSize (1.8.0) is the per-user initial/per-page item count the browse
/// view requests as it infinite-scrolls. 0 means unset (the frontend applies its
/// default of 50), so rows saved before this field existed — and PUTs from older
/// clients that omit it — behave exactly as before. Stored verbatim, never clamped
/// or interpreted server-side (browse still takes pageSize as an explicit query
/// parameter).
///
/// HomeRecentWindowDays (1.12.0 refinement) is the per-user "recently added" window,
/// in days, for the home "New chapters" row. 0 means unset — <c>RecentChaptersService</c>
/// falls back to its 30-day default — and non-zero values are clamped to 1..365 by the
/// service before use, so this DTO stores the raw value verbatim (like LibraryPageSize)
/// and never interprets or clamps it itself. Reusing this preferences blob (rather than
/// a new endpoint) keeps the Settings screen's single "load once, echo back on save"
/// round-trip intact.
///
/// ListColumns (1.18.0) is the per-user list-view column count (1-3) on wide
/// viewports, mirroring CardSize's presentation-only pattern. 0 means unset — the
/// frontend applies its own default (2) — and the server never interprets or clamps
/// this value; it is stored and returned verbatim, like the other presentation fields.
/// </summary>
public sealed record LibraryViewPreferencesDto
{
    public string ViewMode { get; init; } = "grid";
    public string Density { get; init; } = "comfortable";
    public string Sort { get; init; } = "name";
    public string Direction { get; init; } = "";
    public string CardSize { get; init; } = "";
    public int LibraryPageSize { get; init; }
    public int HomeRecentWindowDays { get; init; }
    public int ListColumns { get; init; }

    /// <summary>
    /// Per-user opt-in for the Home "Favorites" row (1.21.0). False (default) hides the
    /// row; the star affordances everywhere else are always present. Mirrors the other
    /// presentation flags: stored verbatim, round-tripped through the existing
    /// library-preferences endpoint, defaults preserve pre-1.21.0 behaviour.
    /// </summary>
    public bool ShowFavoritesHomeRow { get; init; }

    /// <summary>
    /// Per-user opt-in for favorites prominence in search (1.21.0). False (default) =
    /// favorited results render normally (no badge, no reordering). True = favorited
    /// results get a star badge and are boosted to the top of the result list.
    /// </summary>
    public bool FavoritesSearchProminence { get; init; }
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

    /// <summary>
    /// Admin-picked icon name from <see cref="LibraryIcons.Allowed"/>, or
    /// null for the client-derived default (a name-hashed monogram/glyph).
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Effective automatic scan schedule (1.23.0), one of
    /// <see cref="LibraryScanSchedules.Allowed"/> (a library with no stored
    /// schedule reports the daily default). Admin library responses only; null
    /// elsewhere.
    /// </summary>
    public string? ScanSchedule { get; init; }

    /// <summary>
    /// Approximate time of the next automatic scan (1.23.0); a time in the past
    /// means the scan is due and starts at the next scheduler pass. Null when
    /// the schedule is off, the scheduler is disabled, or outside admin
    /// library responses.
    /// </summary>
    public DateTimeOffset? NextScheduledScanAt { get; init; }
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
/// Request to set a library's icon. <see cref="Icon"/> must be a name from
/// <see cref="LibraryIcons.Allowed"/>, or null to clear it back to the
/// client-derived default.
/// </summary>
public sealed record SetLibraryIconRequest
{
    public string? Icon { get; init; }
}

/// <summary>
/// Sets a library's automatic scan schedule (1.23.0): one of
/// <see cref="LibraryScanSchedules.Allowed"/>, or null for the default (daily).
/// </summary>
public sealed record SetLibraryScanScheduleRequest
{
    public string? ScanSchedule { get; init; }
}

/// <summary>
/// Response when a scan is triggered.
/// </summary>
public sealed record ScanTriggeredDto
{
    public required string ScanRunId { get; init; }
}

/// <summary>
/// Response when a scan is triggered for every registered library at once
/// (1.8.0). Libraries already scanning are skipped rather than failing the
/// batch, so <see cref="StartedCount"/> + <see cref="SkippedCount"/> equals the
/// total number of registered libraries. <see cref="ScanRunIds"/> holds the
/// opaque ids of the scans that were actually started, in registration order.
/// </summary>
public sealed record ScanAllResultDto
{
    public required int StartedCount { get; init; }
    public required int SkippedCount { get; init; }
    public required IReadOnlyList<string> ScanRunIds { get; init; }
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
    public bool IsPendingActivation { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastLoginAt { get; init; }
}

/// <summary>
/// Request to create a new user. When <see cref="Password"/> is omitted the
/// server creates the account in pending-activation state and returns a
/// single-use activation URL for the admin to hand to the user.
/// </summary>
public sealed record CreateUserRequest
{
    public required string Username { get; init; }
    public string? Password { get; init; }
    public bool IsAdmin { get; init; }
}

/// <summary>
/// Response from creating a user. When the user was created with a password
/// <see cref="ActivationUrl"/> is null. When created without a password the
/// activation URL is returned exactly once — it is never stored or logged in
/// cleartext on the server.
/// </summary>
public sealed record CreateUserResponse
{
    public required AdminUserDto User { get; init; }
    public string? ActivationUrl { get; init; }
}

/// <summary>
/// Request to activate a pending account by setting its initial password.
/// The token is the raw activation token from the URL the admin shared.
/// </summary>
public sealed record ActivateAccountRequest
{
    public required string Token { get; init; }
    public required string Password { get; init; }
}

/// <summary>
/// Response from reissuing an activation link (1.17.0) for a user created
/// passwordless who has not yet activated their account. Same payload shape
/// as <see cref="CreateUserResponse"/> for a pending-activation user: the
/// previous token is invalidated and a fresh one is returned exactly once.
/// </summary>
public sealed record ReissueActivationResponse
{
    public required AdminUserDto User { get; init; }
    public required string ActivationUrl { get; init; }
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
// .yacreaderlibrary/library.ydb SQLite database into MangaPixer reading
// progress + sticky read-marks for a chosen target user. Covers and
// thumbnails are not imported (MangaPixer regenerates them). Source media is
// never mutated: the ydb is opened read-only, or copied to an app-owned
// scratch snapshot first (default). Existing MangaPixer state is never
// overwritten unless the admin explicitly opts in via Overwrite.

/// <summary>
/// Request for a YACReader progress import (preview or apply). The admin
/// selects a MangaPixer library to map into, the path to the YACReader
/// library database (the .yacreaderlibrary directory or the library.ydb
/// file), and the target MangaPixer user to receive the imported progress.
/// </summary>
public sealed record YacReaderImportRequest
{
    /// <summary>Opaque public id of the MangaPixer library to map into.</summary>
    public required string LibraryId { get; init; }

    /// <summary>
    /// Optional explicit path to the YACReader library database (the
    /// .yacreaderlibrary directory or the library.ydb file). A private server
    /// locator, never echoed in responses. When omitted, the server auto-detects
    /// <c>.yacreaderlibrary/library.ydb</c> inside the mapped library's own root —
    /// the normal case, so the client never handles a path.
    /// </summary>
    public string? YacDbPath { get; init; }

    /// <summary>Opaque public id of the MangaPixer user to import progress for.</summary>
    public required string TargetUserId { get; init; }

    /// <summary>
    /// When false (default), items that already have MangaPixer progress are
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
/// Result of detecting a YACReader library inside a MangaPixer library's root.
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
/// exposed; only the mapped MangaPixer item id/display name and the imported
/// reading state are surfaced.
/// </summary>
public sealed record YacReaderImportItemDto
{
    /// <summary>Opaque public id of the mapped MangaPixer archive item, or null if unmapped.</summary>
    public string? ItemId { get; init; }

    /// <summary>Display name of the mapped MangaPixer item, or null if unmapped.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Whether YACReader marked the comic as read (finished).</summary>
    public required bool Read { get; init; }

    /// <summary>Whether YACReader recorded the comic as having been opened.</summary>
    public required bool HasBeenOpened { get; init; }

    /// <summary>YACReader's 1-based current page (0 if unknown).</summary>
    public required int CurrentPage { get; init; }

    /// <summary>Resolved MangaPixer reading state: "unread", "inProgress", or "completed".</summary>
    public required string State { get; init; }

    /// <summary>True if MangaPixer already has progress for this item (a conflict).</summary>
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

    /// <summary>Mapped items that already have MangaPixer progress (conflicts).</summary>
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

    /// <summary>Mapped items skipped because MangaPixer already had progress (overwrite=false).</summary>
    public required int Skipped { get; init; }

    /// <summary>Sticky read-marks set (for comics YACReader marked read).</summary>
    public required int ReadMarks { get; init; }
}

/// <summary>
/// Read-only product version information. The version comes
/// from the assembly <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/>,
/// which is sourced from <c>Version.props</c> at build time (see
/// <c>Directory.Build.props</c>). Contains no private data; served unauthenticated
/// so the app footer can display it before login.
/// </summary>
public sealed record SystemInfoDto
{
    /// <summary>
    /// The full product version (SemVer plus optional build metadata, e.g.
    /// "1.3.0+sha.abc123"). This is the <c>InformationalVersion</c>, not the
    /// numeric <c>AssemblyVersion</c> (which stays at major.minor.0.0).
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// The server's operating system, so the admin UI can speak the server's
    /// platform idiom (e.g. filesystem paths) instead of assuming a container
    /// (added in 1.13.0). <c>"windows"</c> or <c>"linux"</c>; null on
    /// any other OS. Not sensitive — no paths or hostnames.
    /// </summary>
    public string? Platform { get; init; }
}

/// <summary>
/// Status of the optional Update Checker (1.21.0), returned by
/// <c>GET /api/v1/operations/update-check</c> (admin-only). The checker is OFF by
/// default and is the single sanctioned outbound third-party call: when enabled it
/// compares the running version against the latest GitHub release. Carries only
/// version strings and a timestamp — no instance identifier, path, or telemetry.
/// </summary>
public sealed record UpdateCheckStatusDto
{
    /// <summary>Whether the admin has opted in to update checking.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The running server version (informational version, no leading "v").</summary>
    public required string CurrentVersion { get; init; }

    /// <summary>
    /// The latest release version last learned from GitHub (no leading "v"), or
    /// null if the check is off or has never successfully run.
    /// </summary>
    public string? LatestVersion { get; init; }

    /// <summary>True when <see cref="LatestVersion"/> is strictly newer than <see cref="CurrentVersion"/>.</summary>
    public required bool UpdateAvailable { get; init; }

    /// <summary>UTC time of the last completed check attempt, or null if never checked.</summary>
    public DateTimeOffset? LastChecked { get; init; }
}

/// <summary>
/// Request to change the Update Checker opt-in, sent to
/// <c>PUT /api/v1/operations/update-check/settings</c> (admin-only).
/// </summary>
public sealed record UpdateCheckSettingsRequest
{
    /// <summary>Whether update checking should be enabled.</summary>
    public required bool Enabled { get; init; }
}
