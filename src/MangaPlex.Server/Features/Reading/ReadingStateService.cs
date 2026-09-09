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
    /// Returns null if the user lacks access.
    /// For an item with no progress, returns 200 with State = Unread,
    /// PageIndex = 0, Revision = 0 (audit defect D14/D32 — eliminates
    /// the browser 404 for unread items).
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

        var item = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == itemId, ct);

        if (progress is null)
        {
            // No progress record — return unread state instead of null
            return new ReadingProgressDto
            {
                ItemId = OpaqueId.Encode(itemId),
                PageIndex = 0,
                ContentVersion = item?.ContentVersion ?? 0,
                UpdatedAt = DateTimeOffset.UtcNow,
                State = ReadingState.Unread,
                Revision = 0,
                IsStale = false,
            };
        }

        var isStale = item is not null && progress.ContentVersion != item.ContentVersion;

        return new ReadingProgressDto
        {
            ItemId = OpaqueId.Encode(itemId),
            PageIndex = progress.Ordinal,
            ContentVersion = progress.ContentVersion,
            UpdatedAt = progress.UpdatedAt,
            State = (ReadingState)progress.State,
            Revision = progress.Revision,
            IsStale = isStale,
        };
    }

    /// <summary>
    /// Updates reading progress. Revisioned and idempotent.
    /// Returns false if authorization fails or content version mismatch.
    /// </summary>
    /// <param name="expectedRevision">
    /// The revision the client believes the progress is at, or null for
    /// the first write (If-None-Match: *). A mismatch returns
    /// <see cref="UpdateStatus.PreconditionFailed"/> (audit defect D32).
    /// </param>
    public async Task<UpdateProgressResult> UpdateProgressAsync(
        long userId,
        long itemId,
        int pageIndex,
        long expectedContentVersion,
        string mutationId,
        long? expectedRevision = null,
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

        // Idempotency: if the mutation ID matches, skip (return current revision)
        if (progress is not null && !string.IsNullOrEmpty(progress.LastMutationId)
            && progress.LastMutationId == mutationId)
            return UpdateProgressResult.Success(progress.Revision, alreadyApplied: true);

        // Optimistic concurrency: If-Match revision check (audit defect D32)
        if (expectedRevision.HasValue)
        {
            var currentRevision = progress?.Revision ?? 0;
            if (currentRevision != expectedRevision.Value)
                return UpdateProgressResult.PreconditionFailed(currentRevision);
        }

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
                // Actively reading it again un-dismisses it from continue-reading.
                progress.HiddenFromContinue = false;
                if (isCompleted && !progress.CompletedAt.HasValue)
                    progress.CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        // Sticky read-mark: reaching the last page auto-marks the item read (1.2.0).
        // Tracked here so it commits atomically with the progress row. Backward
        // navigation takes the re-reading branch above (isCompleted == false), so it
        // never removes the mark.
        if (isCompleted)
            await EnsureReadMarkTrackedAsync(userId, itemId, "completion", ct);

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
    /// Dismisses an item from the user's "continue reading" strip (1.2.0) without
    /// marking it read. Sticky until the user makes forward progress on it again.
    /// Returns false if access is denied. A no-op (still true) if there is no
    /// progress row — an item not in the strip is already effectively dismissed.
    /// </summary>
    public async Task<bool> DismissFromContinueAsync(
        long userId,
        long itemId,
        CancellationToken ct = default)
    {
        if (!await _auth.CanAccessItemAsync(userId, itemId, ct))
            return false;

        var progress = await _db.ReadingProgress
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == itemId, ct);
        if (progress is null)
            return true;

        if (!progress.HiddenFromContinue)
        {
            progress.HiddenFromContinue = true;
            await _db.SaveChangesAsync(ct);
        }
        return true;
    }

    // --- Sticky read-marks (1.2.0) ---
    //
    // A read-mark is a separate, sticky flag decoupled from reading position: its
    // presence means "read". It is set on completion, or manually (per item, or in
    // bulk over a folder's descendant archives), and only an explicit clear/reset
    // removes it — re-reading never does.

    /// <summary>
    /// Returns whether the user has marked the item read. False if access is denied.
    /// </summary>
    public async Task<bool> IsReadAsync(long userId, long itemId, CancellationToken ct = default)
    {
        if (!await _auth.CanAccessItemAsync(userId, itemId, ct))
            return false;

        return await _db.ReadMarks.AnyAsync(m => m.UserId == userId && m.ItemId == itemId, ct);
    }

    /// <summary>
    /// Sets or clears the sticky read-mark for a single item. Idempotent.
    /// Returns false only if the user lacks access to the item.
    /// </summary>
    public async Task<bool> SetItemReadAsync(
        long userId,
        long itemId,
        bool read,
        CancellationToken ct = default)
    {
        if (!await _auth.CanAccessItemAsync(userId, itemId, ct))
            return false;

        var existing = await _db.ReadMarks
            .FirstOrDefaultAsync(m => m.UserId == userId && m.ItemId == itemId, ct);

        if (read && existing is null)
        {
            _db.ReadMarks.Add(new ReadMarkEntity
            {
                UserId = userId,
                ItemId = itemId,
                MarkedAt = DateTimeOffset.UtcNow,
                Source = "manual",
            });
            await _db.SaveChangesAsync(ct);
        }
        else if (!read && existing is not null)
        {
            _db.ReadMarks.Remove(existing);
            await _db.SaveChangesAsync(ct);
        }

        return true;
    }

    /// <summary>
    /// Sets or clears read-marks in bulk across every readable descendant archive of a
    /// folder. Returns null if the node is missing, not a folder, or inaccessible.
    /// </summary>
    public async Task<BulkReadMarkResultDto?> SetFolderReadAsync(
        long userId,
        long folderNodeId,
        bool read,
        CancellationToken ct = default)
    {
        var folder = await _db.CatalogNodes
            .FirstOrDefaultAsync(n => n.Id == folderNodeId, ct);
        if (folder is null || folder.Kind != (int)CatalogNodeKind.Folder)
            return null;

        if (!await _auth.CanAccessLibraryAsync(userId, folder.LibraryId, ct))
            return null;

        var archiveIds = await GetDescendantArchiveIdsAsync(folderNodeId, ct);
        if (archiveIds.Count == 0)
            return new BulkReadMarkResultDto { Affected = 0, Total = 0 };

        var already = await _db.ReadMarks
            .Where(m => m.UserId == userId && archiveIds.Contains(m.ItemId))
            .Select(m => m.ItemId)
            .ToListAsync(ct);
        var alreadySet = already.ToHashSet();

        int affected;
        if (read)
        {
            var now = DateTimeOffset.UtcNow;
            var toAdd = archiveIds.Where(id => !alreadySet.Contains(id)).ToList();
            foreach (var id in toAdd)
            {
                _db.ReadMarks.Add(new ReadMarkEntity
                {
                    UserId = userId,
                    ItemId = id,
                    MarkedAt = now,
                    Source = "bulk",
                });
            }
            affected = toAdd.Count;
        }
        else
        {
            var toRemove = await _db.ReadMarks
                .Where(m => m.UserId == userId && archiveIds.Contains(m.ItemId))
                .ToListAsync(ct);
            _db.ReadMarks.RemoveRange(toRemove);
            affected = toRemove.Count;
        }

        if (affected > 0)
            await _db.SaveChangesAsync(ct);

        return new BulkReadMarkResultDto { Affected = affected, Total = archiveIds.Count };
    }

    /// <summary>
    /// Ensures a read-mark exists for (user, item) as part of the current change set,
    /// without saving. Callers commit it with their own <c>SaveChangesAsync</c>.
    /// </summary>
    private async Task EnsureReadMarkTrackedAsync(
        long userId,
        long itemId,
        string source,
        CancellationToken ct)
    {
        var exists = await _db.ReadMarks
            .AnyAsync(m => m.UserId == userId && m.ItemId == itemId, ct);
        if (!exists)
        {
            _db.ReadMarks.Add(new ReadMarkEntity
            {
                UserId = userId,
                ItemId = itemId,
                MarkedAt = DateTimeOffset.UtcNow,
                Source = source,
            });
        }
    }

    /// <summary>
    /// Collects the internal node ids of every non-tombstoned archive under a folder,
    /// at any depth, via a recursive descent over <c>catalog_nodes.ParentId</c>.
    /// </summary>
    private async Task<List<long>> GetDescendantArchiveIdsAsync(long folderNodeId, CancellationToken ct)
    {
        var ids = new List<long>();
        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE descendants(Id) AS (
                    SELECT Id FROM catalog_nodes WHERE ParentId = $root
                    UNION ALL
                    SELECT cn.Id FROM catalog_nodes cn
                    JOIN descendants d ON cn.ParentId = d.Id
                )
                SELECT cn.Id
                FROM catalog_nodes cn
                JOIN descendants d ON cn.Id = d.Id
                WHERE cn.Kind = 1 AND cn.Availability != 5;
                """;
            var p = command.CreateParameter();
            p.ParameterName = "$root";
            p.Value = folderNodeId;
            command.Parameters.Add(p);

            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                ids.Add(reader.GetInt64(0));
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
        return ids;
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

        // DateTimeOffset is stored as a comparable long via
        // DateTimeOffsetToBinaryConverter (see MangaPlexDbContext.ConfigureConventions),
        // so ORDER BY is now translated server-side. The previous client-side
        // sort workaround (audit defect D26) has been removed.
        // Exclude items the user dismissed from the strip, and items they have
        // marked read (1.2.0): the strip stays focused on what's actually mid-read.
        return await (
            from p in _db.ReadingProgress
            join n in _db.CatalogNodes on p.ItemId equals n.Id
            where p.UserId == userId
               && p.State == (int)ReadingState.InProgress
               && !p.HiddenFromContinue
               && !_db.ReadMarks.Any(m => m.UserId == userId && m.ItemId == p.ItemId)
               && accessibleLibs.Contains(n.LibraryId)
               && n.Availability != (int)CatalogNodeAvailability.Tombstoned
            orderby p.UpdatedAt descending
            select new ContinueReadingEntry
            {
                ItemId = n.PublicId,
                DisplayName = n.DisplayName,
                PageIndex = p.Ordinal,
                ContentVersion = p.ContentVersion,
                UpdatedAt = p.UpdatedAt,
            })
            .Take(limit)
            .ToListAsync(ct);
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

    public static UpdateProgressResult PreconditionFailed(long? currentRevision) =>
        new() { Status = UpdateStatus.PreconditionFailed, Error = "Revision mismatch", Revision = currentRevision };
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
    PreconditionFailed = 5,
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
