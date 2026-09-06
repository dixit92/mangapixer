namespace com.lifepixer.mangaplex.Server.Features.Reading;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Core.Reading;
using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Reading state service. Provides revisioned/idempotent progress,
/// bookmarks, preferences, continue-reading, completed/reread semantics,
/// and stale-manifest mapping.
///
/// Rules:
/// - Two users never silently overwrite independent state.
/// - Two devices for the same user use optimistic concurrency (Revision).
/// - Retries are idempotent (LastMutationId deduplication).
/// - Backwards reading does not mark as completed.
/// - Unread/reset clears progress.
/// - Stale manifest mapping: if content version changed, progress is preserved
///   but marked stale. The client can map by ordinal or entry key.
/// - Authorization is checked before any progress operation.
/// </summary>
public sealed class ReadingStateService
{
    private readonly MangaPlexDbContext _db;
    private readonly LibraryAuthorizationService _auth;

    public ReadingStateService(MangaPlexDbContext db, LibraryAuthorizationService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <summary>
    /// Gets the reading progress for a specific user and item.
    /// Returns null if the user lacks access or no progress exists.
    /// </summary>
    public async Task<ReadingProgressDto?> GetProgressAsync(
        long userId,
        long itemId,
        CancellationToken ct = default)
    {
        if (!await _auth.CanAccessItemAsync(userId, itemId, ct))
            return null;

        var progress = await _db.ReadingProgress
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == itemId, ct);
        if (progress is null)
            return null;

        var item = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == itemId, ct);
        var isStale = item is not null && progress.ContentVersion != item.ContentVersion;

        return new ReadingProgressDto
        {
            ItemId = OpaqueId.Encode(itemId),
            PageIndex = progress.Ordinal,
            ContentVersion = progress.ContentVersion,
            UpdatedAt = progress.UpdatedAt,
            State = (ReadingState)progress.State,
            IsStale = isStale,
        };
    }

    /// <summary>
    /// Updates reading progress. Revisioned and idempotent.
    /// Returns false if authorization fails or content version mismatch.
    /// </summary>
    public async Task<UpdateProgressResult> UpdateProgressAsync(
        long userId,
        long itemId,
        int pageIndex,
        long expectedContentVersion,
        long mutationId,
        double normalizedAnchor = 0.0,
        CancellationToken ct = default)
    {
        if (!await _auth.CanAccessItemAsync(userId, itemId, ct))
            return UpdateProgressResult.Unauthorized();

        // Validate content version
        var item = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == itemId, ct);
        if (item is null)
            return UpdateProgressResult.NotFound();

        if (item.ContentVersion != expectedContentVersion)
            return UpdateProgressResult.StaleContent();

        // Validate page index
        if (pageIndex < 0)
            return UpdateProgressResult.Invalid("Page index cannot be negative.");
        if (item.PageCount.HasValue && pageIndex >= item.PageCount.Value)
            return UpdateProgressResult.Invalid($"Page index {pageIndex} out of range (0..{item.PageCount.Value - 1}).");

        // Find existing progress
        var progress = await _db.ReadingProgress
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == itemId, ct);

        // Idempotency: if the mutation ID is the same or older, skip
        if (progress is not null && mutationId <= progress.LastMutationId)
            return UpdateProgressResult.Success(progress.Revision, alreadyApplied: true);

        var pageCount = item.PageCount ?? 0;
        var isCompleted = pageCount > 0 && pageIndex >= pageCount - 1;
        var state = isCompleted ? (int)ReadingState.Completed : (int)ReadingState.InProgress;

        if (progress is null)
        {
            progress = new ReadingProgressEntity
            {
                UserId = userId,
                ItemId = itemId,
                ContentVersion = expectedContentVersion,
                EntryKey = OpaqueId.Encode(pageIndex),
                Ordinal = pageIndex,
                NormalizedAnchor = normalizedAnchor,
                State = state,
                Revision = 1,
                LastMutationId = mutationId,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = isCompleted ? DateTimeOffset.UtcNow : null,
            };
            _db.ReadingProgress.Add(progress);
        }
        else
        {
            // Backwards reading does not un-complete
            if (progress.State == (int)ReadingState.Completed && pageIndex < progress.Ordinal)
            {
                // Allow re-reading: update position but keep completed state
                progress.Ordinal = pageIndex;
                progress.NormalizedAnchor = normalizedAnchor;
                progress.LastMutationId = mutationId;
                progress.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                progress.ContentVersion = expectedContentVersion;
                progress.EntryKey = OpaqueId.Encode(pageIndex);
                progress.Ordinal = pageIndex;
                progress.NormalizedAnchor = normalizedAnchor;
                progress.State = state;
                progress.Revision++;
                progress.LastMutationId = mutationId;
                progress.UpdatedAt = DateTimeOffset.UtcNow;
                if (isCompleted && !progress.CompletedAt.HasValue)
                    progress.CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        await _db.SaveChangesAsync(ct);
        return UpdateProgressResult.Success(progress.Revision, alreadyApplied: false);
    }

    /// <summary>
    /// Resets progress to unread for a specific item.
    /// </summary>
    public async Task<bool> ResetProgressAsync(
        long userId,
        long itemId,
        CancellationToken ct = default)
    {
        if (!await _auth.CanAccessItemAsync(userId, itemId, ct))
            return false;

        var progress = await _db.ReadingProgress
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == itemId, ct);
        if (progress is null)
            return true; // Already unread

        _db.ReadingProgress.Remove(progress);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Gets continue-reading items for a user (in-progress, most recently updated first).
    /// </summary>
    public async Task<IReadOnlyList<ContinueReadingEntry>> GetContinueReadingAsync(
        long userId,
        int limit = 20,
        CancellationToken ct = default)
    {
        var accessibleLibs = await _auth.GetAccessibleLibraryIdsAsync(userId, ct);

        // SQLite doesn't support DateTimeOffset in ORDER BY, so fetch first, sort on client
        var entries = await (
            from p in _db.ReadingProgress
            join n in _db.CatalogNodes on p.ItemId equals n.Id
            where p.UserId == userId
               && p.State == (int)ReadingState.InProgress
               && accessibleLibs.Contains(n.LibraryId)
               && n.Availability != (int)CatalogNodeAvailability.Tombstoned
            select new ContinueReadingEntry
            {
                ItemId = n.PublicId,
                DisplayName = n.DisplayName,
                PageIndex = p.Ordinal,
                ContentVersion = p.ContentVersion,
                UpdatedAt = p.UpdatedAt,
            })
            .ToListAsync(ct);

        return entries
            .OrderByDescending(e => e.UpdatedAt)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Gets all bookmarks for a user on a specific item.
    /// </summary>
    public async Task<IReadOnlyList<BookmarkEntry>> GetBookmarksAsync(
        long userId,
        long itemId,
        CancellationToken ct = default)
    {
        if (!await _auth.CanAccessItemAsync(userId, itemId, ct))
            return [];

        return await _db.Bookmarks
            .Where(b => b.UserId == userId && b.ItemId == itemId)
            .OrderBy(b => b.Ordinal)
            .Select(b => new BookmarkEntry
            {
                Id = OpaqueId.Encode(b.Id),
                ItemId = OpaqueId.Encode(b.ItemId),
                Ordinal = b.Ordinal,
                NormalizedAnchor = b.NormalizedAnchor,
                Label = b.Label,
                CreatedAt = b.CreatedAt,
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Adds a bookmark for a user on an item.
    /// </summary>
    public async Task<string?> AddBookmarkAsync(
        long userId,
        long itemId,
        int ordinal,
        double normalizedAnchor,
        string? label,
        CancellationToken ct = default)
    {
        if (!await _auth.CanAccessItemAsync(userId, itemId, ct))
            return null;

        var item = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == itemId, ct);
        var contentVersion = item?.ContentVersion ?? 0;

        var bookmark = new BookmarkEntity
        {
            UserId = userId,
            ItemId = itemId,
            ContentVersion = contentVersion,
            EntryKey = OpaqueId.Encode(ordinal),
            Ordinal = ordinal,
            NormalizedAnchor = normalizedAnchor,
            Label = label,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Bookmarks.Add(bookmark);
        await _db.SaveChangesAsync(ct);

        return OpaqueId.Encode(bookmark.Id);
    }

    /// <summary>
    /// Removes a bookmark by its ID. Returns false if not found or unauthorized.
    /// </summary>
    public async Task<bool> RemoveBookmarkAsync(
        long userId,
        string bookmarkPublicId,
        CancellationToken ct = default)
    {
        var bookmarkId = OpaqueId.Decode(bookmarkPublicId);
        var bookmark = await _db.Bookmarks.FirstOrDefaultAsync(b => b.Id == bookmarkId, ct);
        if (bookmark is null || bookmark.UserId != userId)
            return false;

        _db.Bookmarks.Remove(bookmark);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Gets reader preferences for a user.
    /// </summary>
    public async Task<UserPreferencesDto> GetPreferencesAsync(
        long userId,
        CancellationToken ct = default)
    {
        var prefs = await _db.ReaderPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (prefs is null)
            return new UserPreferencesDto();

        return new UserPreferencesDto
        {
            DefaultReaderMode = (ReaderMode)prefs.DefaultReaderMode,
            PreferDoubleSpread = prefs.PreferDoubleSpread,
            ReducedMotion = prefs.ReducedMotion,
            PreferredBackground = prefs.PreferredBackground,
        };
    }

    /// <summary>
    /// Updates reader preferences for a user. Creates if not exists.
    /// </summary>
    public async Task SetPreferencesAsync(
        long userId,
        UserPreferencesDto preferences,
        CancellationToken ct = default)
    {
        var prefs = await _db.ReaderPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (prefs is null)
        {
            prefs = new ReaderPreferencesEntity
            {
                UserId = userId,
                DefaultReaderMode = (int)preferences.DefaultReaderMode,
                PreferDoubleSpread = preferences.PreferDoubleSpread,
                ReducedMotion = preferences.ReducedMotion,
                PreferredBackground = preferences.PreferredBackground,
            };
            _db.ReaderPreferences.Add(prefs);
        }
        else
        {
            prefs.DefaultReaderMode = (int)preferences.DefaultReaderMode;
            prefs.PreferDoubleSpread = preferences.PreferDoubleSpread;
            prefs.ReducedMotion = preferences.ReducedMotion;
            prefs.PreferredBackground = preferences.PreferredBackground;
        }

        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Result of a progress update operation.
/// </summary>
public sealed record UpdateProgressResult
{
    public required UpdateStatus Status { get; init; }
    public long? Revision { get; init; }
    public bool AlreadyApplied { get; init; }
    public string? Error { get; init; }

    public static UpdateProgressResult Success(long revision, bool alreadyApplied) =>
        new() { Status = UpdateStatus.Success, Revision = revision, AlreadyApplied = alreadyApplied };

    public static UpdateProgressResult Unauthorized() =>
        new() { Status = UpdateStatus.Unauthorized, Error = "Access denied" };

    public static UpdateProgressResult NotFound() =>
        new() { Status = UpdateStatus.NotFound, Error = "Item not found" };

    public static UpdateProgressResult StaleContent() =>
        new() { Status = UpdateStatus.StaleContent, Error = "Content version mismatch" };

    public static UpdateProgressResult Invalid(string error) =>
        new() { Status = UpdateStatus.Invalid, Error = error };
}

/// <summary>
/// Status of a progress update.
/// </summary>
public enum UpdateStatus
{
    Success = 0,
    Unauthorized = 1,
    NotFound = 2,
    StaleContent = 3,
    Invalid = 4,
}

/// <summary>
/// A continue-reading entry.
/// </summary>
public sealed record ContinueReadingEntry
{
    public required string ItemId { get; init; }
    public required string DisplayName { get; init; }
    public required int PageIndex { get; init; }
    public required long ContentVersion { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// A bookmark entry.
/// </summary>
public sealed record BookmarkEntry
{
    public required string Id { get; init; }
    public required string ItemId { get; init; }
    public required int Ordinal { get; init; }
    public required double NormalizedAnchor { get; init; }
    public string? Label { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
