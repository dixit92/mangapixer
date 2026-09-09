namespace com.lifepixer.mangaplex.Server.Persistence.Entities;

using com.lifepixer.mangaplex.Core.Media;

/// <summary>
/// Library entity. Maps to a real root directory on the filesystem.
/// The root path is private and never exposed in DTOs.
/// </summary>
public sealed class LibraryEntity
{
    public long Id { get; set; }
    public string PublicId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Private root path. Never exposed in API DTOs.
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    public string CaseComparisonPolicy { get; set; } = "ordinal";

    /// <summary>
    /// Observed root identity (volume serial / device ID / inode) for mount-change detection.
    /// </summary>
    public string? RootIdentity { get; set; }

    public string State { get; set; } = "active";

    /// <summary>
    /// Monotonically increasing catalog revision. Bumped on every scan reconciliation.
    /// </summary>
    public long CatalogRevision { get; set; }

    public string? ScanSchedule { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastScanCompleted { get; set; }

    /// <summary>
    /// Global default reader mode for the whole library (1.2.0). Null = inherit
    /// (fall through to the user's personal default). Stored as the ReaderMode enum's
    /// int value. Admin-set; applies to all users. Overridable per folder — see
    /// <see cref="FolderReaderDefaultEntity"/>.
    /// </summary>
    public int? DefaultReaderMode { get; set; }

    public ICollection<LibraryGrantEntity> Grants { get; set; } = [];
    public ICollection<CatalogNodeEntity> Nodes { get; set; } = [];
}

/// <summary>
/// Global per-folder default reader mode override (1.2.0, admin-set). Applies to the
/// folder and all its subfolders; during resolution the nearest ancestor with an
/// override wins. One row per folder that has an explicit override — absence means
/// "inherit". <see cref="ReaderMode"/> stored as its int value.
/// </summary>
public sealed class FolderReaderDefaultEntity
{
    public long Id { get; set; }
    public long NodeId { get; set; }
    public int ReaderMode { get; set; }
    public CatalogNodeEntity? Node { get; set; }
}

/// <summary>
/// Per-user library access grant. Admins manage all libraries without explicit grants.
/// </summary>
public sealed class LibraryGrantEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long LibraryId { get; set; }
    public DateTimeOffset GrantedAt { get; set; }

    public UserEntity? User { get; set; }
    public LibraryEntity? Library { get; set; }
}

/// <summary>
/// Catalog node: a folder or archive in the library tree.
/// </summary>
public sealed class CatalogNodeEntity
{
    public long Id { get; set; }
    public string PublicId { get; set; } = string.Empty;
    public long LibraryId { get; set; }
    public long? ParentId { get; set; }

    /// <summary>
    /// 0 = folder, 1 = archive.
    /// </summary>
    public int Kind { get; set; }

    /// <summary>
    /// Original display name (folder name or archive filename stem).
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Original relative path from library root. Private, never in DTOs.
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Comparison path key for uniqueness constraints.
    /// </summary>
    public string PathKey { get; set; } = string.Empty;

    /// <summary>
    /// Persisted natural sort key for keyset pagination.
    /// </summary>
    public string SortKey { get; set; } = string.Empty;

    /// <summary>
    /// 0=available, 1=preparing, 2=unsupported, 3=corrupt, 4=unavailable, 5=tombstoned.
    /// </summary>
    public int Availability { get; set; }

    /// <summary>
    /// Last scan revision that observed this node.
    /// </summary>
    public long LastSeenScanRevision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public LibraryEntity? Library { get; set; }
    public CatalogNodeEntity? Parent { get; set; }
    public ArchiveItemEntity? ArchiveItem { get; set; }
}

/// <summary>
/// Archive item: analysis state and content metadata for an archive node.
/// </summary>
public sealed class ArchiveItemEntity
{
    /// <summary>
    /// Same as CatalogNodeEntity.Id (1:1).
    /// </summary>
    public long NodeId { get; set; }

    public int ArchiveFormat { get; set; }
    public long ByteLength { get; set; }
    public long ModificationTicks { get; set; }

    /// <summary>
    /// Optional file identity (inode / file ID) for mount-change diagnosis.
    /// </summary>
    public string? FileIdentity { get; set; }

    /// <summary>
    /// Content version. Incremented when source content changes are detected.
    /// </summary>
    public long ContentVersion { get; set; }

    /// <summary>
    /// 0=ready, 1=pending, 2=failed, 3=unsupported, 4=encrypted, 5=missing.
    /// </summary>
    public int AnalysisState { get; set; }

    public string? AnalysisError { get; set; }
    public int? PageCount { get; set; }

    /// <summary>
    /// Optional strong hash (SHA-256) for identity matching.
    /// </summary>
    public string? StrongHash { get; set; }

    /// <summary>
    /// Source version that the strong hash was verified against.
    /// </summary>
    public long? StrongHashSourceVersion { get; set; }

    public DateTimeOffset? LastAnalyzedAt { get; set; }

    public CatalogNodeEntity? Node { get; set; }
    public ICollection<PageEntryEntity> Pages { get; set; } = [];
}

/// <summary>
/// Page entry within an archive item's manifest.
/// </summary>
public sealed class PageEntryEntity
{
    public long Id { get; set; }
    public long ItemId { get; set; }
    public long ContentVersion { get; set; }

    /// <summary>
    /// Zero-based ordinal in reading order.
    /// </summary>
    public int Ordinal { get; set; }

    /// <summary>
    /// Stable entry key (opaque, used in page fetch URLs).
    /// </summary>
    public string EntryKey { get; set; } = string.Empty;

    /// <summary>
    /// Safe source-entry locator (internal only, never in DTOs).
    /// </summary>
    public string SourceEntryLocator { get; set; } = string.Empty;

    public string MediaType { get; set; } = string.Empty;
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>
    /// 0=notAnimated, 1=animated, 2=animationError, 3=unknown.
    /// </summary>
    public int AnimationState { get; set; }

    /// <summary>
    /// 0=supported, 1=error, 2=unsupported.
    /// </summary>
    public int PageState { get; set; }

    public long ByteSize { get; set; }

    public ArchiveItemEntity? Item { get; set; }
}

/// <summary>
/// Per-user reading progress for an item.
/// </summary>
public sealed class ReadingProgressEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long ItemId { get; set; }

    public long ContentVersion { get; set; }
    public string EntryKey { get; set; } = string.Empty;
    public int Ordinal { get; set; }

    /// <summary>
    /// Normalized vertical anchor (0.0-1.0) for webtoon mode resume.
    /// </summary>
    public double NormalizedAnchor { get; set; }

    /// <summary>
    /// 0=unread, 1=inProgress, 2=completed.
    /// </summary>
    public int State { get; set; }

    /// <summary>
    /// Application-managed revision for optimistic concurrency.
    /// </summary>
    public long Revision { get; set; }

    /// <summary>
    /// Client-generated mutation ID (ULID or GUID string) for idempotent
    /// retries. A duplicate mutation ID is a no-op. Stored as a string
    /// (audit defect D32 — was long, but the contract requires a client-
    /// generated string ID).
    /// </summary>
    public string LastMutationId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public UserEntity? User { get; set; }
}

/// <summary>
/// Per-user sticky "read" mark for an item (1.2.0). A separate, sticky flag —
/// decoupled from <see cref="ReadingProgressEntity"/> position. The mere presence
/// of a row means "read"; there is no unread row.
///
/// Semantics (owner-settled 2026-09-08):
/// - Set/cleared manually without opening the item (or in bulk over a folder's
///   descendant archives).
/// - Auto-set when the user completes an item (reaches the last page).
/// - Sticky: navigating back to earlier pages never removes it. Only an explicit
///   clear (or reset) marks the item unread again.
/// </summary>
public sealed class ReadMarkEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }

    /// <summary>
    /// The archive item's catalog node id (matches <see cref="CatalogNodeEntity.Id"/>).
    /// </summary>
    public long ItemId { get; set; }

    /// <summary>
    /// When the item was marked read (completion time, or the manual-mark time).
    /// </summary>
    public DateTimeOffset MarkedAt { get; set; }

    /// <summary>
    /// How the mark was created: "manual", "completion", or "bulk". Diagnostic only.
    /// </summary>
    public string Source { get; set; } = "manual";

    public UserEntity? User { get; set; }
}

/// <summary>
/// Per-user reader preferences.
/// </summary>
public sealed class ReaderPreferencesEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }

    /// <summary>
    /// 0=pagedLtr, 1=pagedRtl, 2=doubleSpread, 3=verticalWebtoon.
    /// </summary>
    public int DefaultReaderMode { get; set; }

    public bool PreferDoubleSpread { get; set; }
    public bool ReducedMotion { get; set; }
    public string? PreferredBackground { get; set; }

    public UserEntity? User { get; set; }
}

/// <summary>
/// Per-item reader preference overrides.
/// </summary>
public sealed class ItemReaderOverridesEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long ItemId { get; set; }

    public int? ReaderMode { get; set; }
    public int? Direction { get; set; }
    public int? FitMode { get; set; }
    public int? SpreadOffset { get; set; }
    public int? CoverOffset { get; set; }
    public string? Background { get; set; }

    public UserEntity? User { get; set; }
}

/// <summary>
/// User bookmark within an item.
/// </summary>
public sealed class BookmarkEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long ItemId { get; set; }
    public long ContentVersion { get; set; }
    public string EntryKey { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public double NormalizedAnchor { get; set; }
    public string? Label { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public UserEntity? User { get; set; }
}

/// <summary>
/// Background job (scan, analysis, hash, etc.).
/// </summary>
public sealed class JobEntity
{
    public long Id { get; set; }
    public string JobType { get; set; } = string.Empty;
    public long? LibraryId { get; set; }
    public long? ItemId { get; set; }

    /// <summary>
    /// 0=pending, 1=running, 2=completed, 3=failed, 4=cancelled, 5=expired.
    /// </summary>
    public int Status { get; set; }

    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiry { get; set; }
    public int Attempt { get; set; }
    public int? ProgressCurrent { get; set; }
    public int? ProgressTotal { get; set; }
    public string? SanitizedError { get; set; }

    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// Scan run state for a library scan.
/// </summary>
public sealed class ScanRunEntity
{
    public long Id { get; set; }
    public long LibraryId { get; set; }
    public long ScanRevision { get; set; }

    /// <summary>
    /// 0=pending, 1=running, 2=completed, 3=failed, 4=cancelled, 5=interrupted.
    /// </summary>
    public int Status { get; set; }

    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiry { get; set; }

    public int NodesObserved { get; set; }
    public int NodesAdded { get; set; }
    public int NodesUpdated { get; set; }
    public int NodesTombstoned { get; set; }
    public string? SanitizedError { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// Bounded-batch staging row for scan observations.
/// </summary>
public sealed class ScanObservationEntity
{
    public long Id { get; set; }
    public long ScanRunId { get; set; }
    public long LibraryId { get; set; }

    public string RelativePath { get; set; } = string.Empty;
    public string PathKey { get; set; } = string.Empty;
    public int Kind { get; set; } // 0=folder, 1=archive
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Relative path of the parent directory; empty string for root-level
    /// entries. Replaces <see cref="ParentNodeId"/> which could not express
    /// parentage before reconciliation (audit defect D1).
    /// </summary>
    public string ParentPathKey { get; set; } = string.Empty;

    /// <summary>
    /// Legacy field — unused after the D1 hierarchy fix. Retained for
    /// schema compatibility with pre-release databases.
    /// </summary>
    public long? ParentNodeId { get; set; }

    public long ByteLength { get; set; }
    public long ModificationTicks { get; set; }
    public string? FileIdentity { get; set; }

    /// <summary>
    /// 0=ok, 1=access-denied, 2=io-error, 3=other.
    /// </summary>
    public int ObservationStatus { get; set; }

    public string? SanitizedError { get; set; }
}

/// <summary>
/// Derived cache entry metadata (file-backed cache, not BLOBs).
/// </summary>
public sealed class CacheEntryEntity
{
    public long Id { get; set; }
    public string CacheKey { get; set; } = string.Empty;
    public int CacheVersion { get; set; }

    /// <summary>
    /// File path in the cache directory (not source media).
    /// </summary>
    public string CacheFilePath { get; set; } = string.Empty;

    public long ByteSize { get; set; }
    public string MediaType { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastAccessedAt { get; set; }

    /// <summary>
    /// 0=active, 1=evicted, 2=corrupt.
    /// </summary>
    public int State { get; set; }
}

/// <summary>
/// Audit event for administrative actions.
/// </summary>
public sealed class AuditEventEntity
{
    public long Id { get; set; }
    public long? ActorUserId { get; set; }
    public long? TargetUserId { get; set; }
    public long? TargetLibraryId { get; set; }
    public long? TargetItemId { get; set; }

    public string Action { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; set; }
    public string? CorrelationId { get; set; }
}

/// <summary>
/// Application user entity. Used with custom ASP.NET Core Identity stores.
/// </summary>
public sealed class UserEntity
{
    public long Id { get; set; }
    public string PublicId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string NormalizedUserName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;
    public string SecurityStamp { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public bool IsAdmin { get; set; } = false;
    public bool ForcePasswordChange { get; set; } = false;

    /// <summary>
    /// Number of failed login attempts. Used for rate limiting / lockout.
    /// </summary>
    public int AccessFailedCount { get; set; }

    /// <summary>
    /// Lockout end time. If in the future, login is blocked.
    /// </summary>
    public DateTimeOffset? LockoutEnd { get; set; }

    /// <summary>
    /// Whether lockout is enabled for this user.
    /// </summary>
    public bool LockoutEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public ICollection<LibraryGrantEntity> LibraryGrants { get; set; } = [];
    public ICollection<ReadingProgressEntity> ReadingProgress { get; set; } = [];
    public ReaderPreferencesEntity? Preferences { get; set; }
}

/// <summary>
/// Session entity for revocable authentication.
/// </summary>
public sealed class SessionEntity
{
    public long Id { get; set; }
    public string TicketId { get; set; } = string.Empty;
    public long UserId { get; set; }
    public string SecurityStamp { get; set; } = string.Empty;

    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }

    public UserEntity? User { get; set; }
}
