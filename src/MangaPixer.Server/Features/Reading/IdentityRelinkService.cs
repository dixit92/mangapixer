namespace com.lifepixer.mangapixer.Server.Features.Reading;

using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Lazy SHA-256 provenance and identity relink service.
///
/// When an archive is moved, duplicated, or replaced, the catalog gets new
/// node IDs. Reading progress is tied to the old node ID. This service:
/// - Computes lazy SHA-256 hashes for items to establish provenance.
/// - Relinks reading progress from old items to new items when the same
///   library contains an item with the same strong hash.
/// - Supports explicit conflict-safe manual relink when auto-relink is ambiguous.
///
/// Rules:
/// - Auto-relink only within the same library (no cross-library relink).
/// - Unique hash relink: if exactly one new item has the same hash, auto-relink.
/// - If multiple items share the hash, require explicit manual relink.
/// - Manual relink is conflict-safe: it refuses to overwrite existing progress
///   on the target item unless explicitly confirmed.
/// - Hash computation is lazy: only computed when needed for relink.
/// </summary>
public sealed class IdentityRelinkService
{
    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _auth;

    public IdentityRelinkService(MangaPixerDbContext db, LibraryAuthorizationService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <summary>
    /// Attempts to auto-relink progress from a tombstoned/missing item to a
    /// new item with the same strong hash in the same library.
    /// Returns the relink result.
    /// </summary>
    public async Task<RelinkResult> AutoRelinkByHashAsync(
        long userId,
        long oldItemId,
        CancellationToken ct = default)
    {
        // Verify the user can access the old item's library
        var oldNode = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.Id == oldItemId, ct);
        if (oldNode is null)
            return RelinkResult.NotFound();

        if (!await _auth.CanAccessLibraryAsync(userId, oldNode.LibraryId, ct))
            return RelinkResult.Unauthorized();

        // Get the old item's strong hash
        var oldItem = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == oldItemId, ct);
        if (oldItem is null || string.IsNullOrEmpty(oldItem.StrongHash))
            return RelinkResult.NoHash();

        // Find new items in the same library with the same hash
        var candidates = await (
            from n in _db.CatalogNodes
            join a in _db.ArchiveItems on n.Id equals a.NodeId
            where n.LibraryId == oldNode.LibraryId
               && n.Id != oldItemId
               && n.Availability != 5 // not tombstoned
               && a.StrongHash == oldItem.StrongHash
            select new { n.Id, n.PublicId, n.DisplayName }
        ).ToListAsync(ct);

        if (candidates.Count == 0)
            return RelinkResult.NoMatch();

        if (candidates.Count > 1)
            return RelinkResult.Ambiguous(candidates.Count);

        // Exactly one match — auto-relink
        var target = candidates[0];
        await RelinkProgressAsync(userId, oldItemId, target.Id, overwrite: false, ct);
        return RelinkResult.Success(target.PublicId, target.DisplayName);
    }

    /// <summary>
    /// Manually relinks progress from one item to another.
    /// Conflict-safe: refuses to overwrite existing progress on the target
    /// unless overwrite is explicitly set to true.
    /// </summary>
    public async Task<RelinkResult> ManualRelinkAsync(
        long userId,
        long oldItemId,
        long targetItemId,
        bool overwrite,
        CancellationToken ct = default)
    {
        var oldNode = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.Id == oldItemId, ct);
        var targetNode = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.Id == targetItemId, ct);
        if (oldNode is null || targetNode is null)
            return RelinkResult.NotFound();

        // User must have access to both libraries
        if (!await _auth.CanAccessLibraryAsync(userId, oldNode.LibraryId, ct))
            return RelinkResult.Unauthorized();
        if (!await _auth.CanAccessLibraryAsync(userId, targetNode.LibraryId, ct))
            return RelinkResult.Unauthorized();

        // Check for existing progress on target
        var existingTarget = await _db.ReadingProgress
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == targetItemId, ct);
        if (existingTarget is not null && !overwrite)
            return RelinkResult.Conflict();

        await RelinkProgressAsync(userId, oldItemId, targetItemId, overwrite, ct);
        return RelinkResult.Success(targetNode.PublicId, targetNode.DisplayName);
    }

    /// <summary>
    /// Records a strong hash for an item (lazy provenance).
    /// Called by the worker after hash computation.
    /// </summary>
    public async Task RecordStrongHashAsync(
        long itemId,
        string strongHash,
        long sourceVersion,
        CancellationToken ct = default)
    {
        var item = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == itemId, ct);
        if (item is null)
            return;

        item.StrongHash = strongHash;
        item.StrongHashSourceVersion = sourceVersion;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Finds items with stale strong hashes (hash computed against an old source version).
    /// These items need re-hashing.
    /// </summary>
    public async Task<IReadOnlyList<long>> GetStaleHashItemsAsync(
        long libraryId,
        long currentScanRevision,
        CancellationToken ct = default)
    {
        return await (
            from n in _db.CatalogNodes
            join a in _db.ArchiveItems on n.Id equals a.NodeId
            where n.LibraryId == libraryId
               && a.StrongHash != null
               && a.StrongHashSourceVersion < currentScanRevision
            select n.Id
        ).ToListAsync(ct);
    }

    private async Task RelinkProgressAsync(
        long userId,
        long oldItemId,
        long targetItemId,
        bool overwrite,
        CancellationToken ct)
    {
        var oldProgress = await _db.ReadingProgress
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == oldItemId, ct);
        if (oldProgress is null)
            return;

        var targetItem = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == targetItemId, ct);
        var targetContentVersion = targetItem?.ContentVersion ?? 0;

        // Pre-check the target. Auto-relink (overwrite=false) onto an item that
        // already has progress would otherwise INSERT and hit the (UserId, ItemId)
        // unique index uncaught — a 500. Keep the target's own progress and just
        // drop the orphaned old row + relink bookmarks: the old item is gone, so
        // its progress is orphaned either way, and the target already represents
        // the same content. (ManualRelinkAsync returns Conflict before reaching
        // here, so this branch only fires for the auto path.)
        var existingTarget = await _db.ReadingProgress
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == targetItemId, ct);
        if (existingTarget is not null && !overwrite)
        {
            _db.ReadingProgress.Remove(oldProgress);
            await RelinkBookmarksAsync(userId, oldItemId, targetItemId, targetContentVersion, ct);
            await _db.SaveChangesAsync(ct);
            return;
        }

        if (overwrite && existingTarget is not null)
            _db.ReadingProgress.Remove(existingTarget);

        // Create new progress on target with same state
        var newProgress = new ReadingProgressEntity
        {
            UserId = userId,
            ItemId = targetItemId,
            ContentVersion = targetContentVersion,
            EntryKey = oldProgress.EntryKey,
            Ordinal = oldProgress.Ordinal,
            NormalizedAnchor = oldProgress.NormalizedAnchor,
            State = oldProgress.State,
            Revision = 1,
            LastMutationId = oldProgress.LastMutationId,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = oldProgress.CompletedAt,
        };
        _db.ReadingProgress.Add(newProgress);

        // Remove old progress
        _db.ReadingProgress.Remove(oldProgress);

        await RelinkBookmarksAsync(userId, oldItemId, targetItemId, targetContentVersion, ct);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // A concurrent request created the target row between our load and our
            // insert. Discard the failed insert and reload the row that now exists,
            // mirroring the UpdateProgressAsync recovery. The old row removal and
            // bookmark relink remain in the change set.
            _db.Entry(newProgress).State = EntityState.Detached;
            var concurrentTarget = await _db.ReadingProgress
                .FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == targetItemId, ct);
            if (concurrentTarget is null)
                throw; // row genuinely gone — surface it
            if (overwrite)
            {
                // Replace the concurrent target's progress with the old item's state.
                concurrentTarget.ContentVersion = targetContentVersion;
                concurrentTarget.EntryKey = oldProgress.EntryKey;
                concurrentTarget.Ordinal = oldProgress.Ordinal;
                concurrentTarget.NormalizedAnchor = oldProgress.NormalizedAnchor;
                concurrentTarget.State = oldProgress.State;
                concurrentTarget.Revision = 1;
                concurrentTarget.LastMutationId = oldProgress.LastMutationId;
                concurrentTarget.UpdatedAt = DateTimeOffset.UtcNow;
                concurrentTarget.CompletedAt = oldProgress.CompletedAt;
            }
            // overwrite=false: keep the concurrent target's own progress.
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Re-points the user's bookmarks from the old item to the target item. The
    /// bookmarks index on (UserId, ItemId) is non-unique, so this never collides
    /// with bookmarks the target may already have.
    /// </summary>
    private async Task RelinkBookmarksAsync(
        long userId, long oldItemId, long targetItemId, long targetContentVersion, CancellationToken ct)
    {
        var oldBookmarks = await _db.Bookmarks
            .Where(b => b.UserId == userId && b.ItemId == oldItemId)
            .ToListAsync(ct);
        foreach (var b in oldBookmarks)
        {
            b.ItemId = targetItemId;
            b.ContentVersion = targetContentVersion;
        }
    }

    /// <summary>
    /// True when a save failed on a SQLite constraint violation (error code 19),
    /// e.g. a concurrent first-write racing on the reading_progress
    /// (UserId, ItemId) unique index.
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        => ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 };
}

/// <summary>
/// Result of a relink operation.
/// </summary>
public sealed record RelinkResult
{
    public required RelinkStatus Status { get; init; }
    public string? TargetItemId { get; init; }
    public string? TargetDisplayName { get; init; }
    public int? CandidateCount { get; init; }
    public string? Error { get; init; }

    public static RelinkResult Success(string targetId, string displayName) =>
        new() { Status = RelinkStatus.Success, TargetItemId = targetId, TargetDisplayName = displayName };

    public static RelinkResult NotFound() =>
        new() { Status = RelinkStatus.NotFound, Error = "Item not found" };

    public static RelinkResult Unauthorized() =>
        new() { Status = RelinkStatus.Unauthorized, Error = "Access denied" };

    public static RelinkResult NoHash() =>
        new() { Status = RelinkStatus.NoHash, Error = "Source item has no strong hash" };

    public static RelinkResult NoMatch() =>
        new() { Status = RelinkStatus.NoMatch, Error = "No matching item found" };

    public static RelinkResult Ambiguous(int count) =>
        new() { Status = RelinkStatus.Ambiguous, CandidateCount = count, Error = $"{count} candidates found" };

    public static RelinkResult Conflict() =>
        new() { Status = RelinkStatus.Conflict, Error = "Target has existing progress" };
}

/// <summary>
/// Status of a relink operation.
/// </summary>
public enum RelinkStatus
{
    Success = 0,
    NotFound = 1,
    Unauthorized = 2,
    NoHash = 3,
    NoMatch = 4,
    Ambiguous = 5,
    Conflict = 6,
}
