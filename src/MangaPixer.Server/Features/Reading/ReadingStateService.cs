namespace com.lifepixer.mangapixer.Server.Features.Reading;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _auth;
    private readonly ILogger<ReadingStateService>? _logger;

    public ReadingStateService(MangaPixerDbContext db, LibraryAuthorizationService auth, ILogger<ReadingStateService>? logger = null)
    {
        _db = db;
        _auth = auth;
        _logger = logger;
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

        // 1.9.0 open-position rule: a READ archive (one carrying a read-mark) reopens
        // from the start in the "finished on the last page" / opted-in cases. Computed
        // here, non-destructively — the stored Ordinal is never rewritten — so the rule
        // is observable over HTTP and toggling the preference is instant and reversible.
        var hasMark = await _db.ReadMarks.AnyAsync(m => m.UserId == userId && m.ItemId == itemId, ct);
        var alwaysFromStart = hasMark && await _db.ReaderPreferences
            .Where(p => p.UserId == userId)
            .Select(p => p.AlwaysOpenReadFromStart)
            .FirstOrDefaultAsync(ct);

        if (progress is null)
        {
            // No progress record — return unread state instead of null. OpenPageIndex is
            // 0 either way (no saved position; a marked-but-unpositioned item also opens
            // at page 1 — the PageCount-unknown manual-mark edge, rule 3).
            return new ReadingProgressDto
            {
                ItemId = OpaqueId.Encode(itemId),
                PageIndex = 0,
                ContentVersion = item?.ContentVersion ?? 0,
                UpdatedAt = DateTimeOffset.UtcNow,
                State = ReadingState.Unread,
                Revision = 0,
                IsStale = false,
                OpenPageIndex = 0,
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
            OpenPageIndex = ComputeOpenPageIndex(hasMark, progress.Ordinal, item?.PageCount, alwaysFromStart),
        };
    }

    /// <summary>
    /// Computes the page index the reader should OPEN at (1.9.0), keyed off POSITION
    /// (Ordinal vs PageCount) rather than the Completed enum so it is robust to the
    /// re-read State-flip. See <see cref="ReadingProgressDto.OpenPageIndex"/>.
    /// </summary>
    internal static int ComputeOpenPageIndex(bool hasMark, int ordinal, int? pageCount, bool alwaysOpenReadFromStart)
    {
        // No read-mark (Unread/Reading): resume exactly where the user left off.
        if (!hasMark)
            return ordinal < 0 ? 0 : ordinal;

        // Read archive, finished on the last page: always reopen from the start,
        // regardless of the preference.
        if (pageCount is int pc && pc > 0 && ordinal >= pc - 1)
            return 0;

        // Read archive, mid-position: start from page 1 only when opted in; otherwise
        // resume the saved (re-read) spot.
        if (alwaysOpenReadFromStart)
            return 0;

        return ordinal < 0 ? 0 : ordinal;
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

        // Applies this write to an existing tracked progress row. Shared by the
        // normal update path and the concurrent-insert recovery below.
        void ApplyUpdate(ReadingProgressEntity p)
        {
            // Backwards reading does not un-complete.
            if (p.State == (int)ReadingState.Completed && pageIndex < p.Ordinal)
            {
                // Allow re-reading: update position but keep completed state.
                p.Ordinal = pageIndex;
                p.NormalizedAnchor = normalizedAnchor;
                p.LastMutationId = mutationId;
                p.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                p.ContentVersion = expectedContentVersion;
                p.EntryKey = OpaqueId.Encode(pageIndex);
                p.Ordinal = pageIndex;
                p.NormalizedAnchor = normalizedAnchor;
                p.State = state;
                p.Revision++;
                p.LastMutationId = mutationId;
                p.UpdatedAt = DateTimeOffset.UtcNow;
                // Actively reading it again un-dismisses it from continue-reading.
                p.HiddenFromContinue = false;
                if (isCompleted && !p.CompletedAt.HasValue)
                    p.CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        var inserted = false;
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
            inserted = true;
        }
        else
        {
            ApplyUpdate(progress);
        }

        // Sticky read-mark: reaching the last page auto-marks the item read (1.2.0).
        // Tracked here so it commits atomically with the progress row. Backward
        // navigation takes the re-reading branch above (isCompleted == false), so it
        // never removes the mark.
        if (isCompleted)
            await EnsureReadMarkTrackedAsync(userId, itemId, "completion", ct);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (inserted && IsUniqueConstraintViolation(ex))
        {
            // Concurrency: another request created the (UserId, ItemId) row between our
            // read and our insert, so the INSERT hit the unique index. Recover by
            // discarding the failed insert, reloading the row that now exists, and
            // re-applying this write as a normal update (last-write-wins on position,
            // with the same backward-reading guard).
            //
            // EF Core logs the failed command at Error level BEFORE this catch runs
            // (RelationalEventId.CommandError), so a recovered race still surfaced as
            // an error in production (~10/24h). The host Serilog filter drops that
            // specific EF log line; this Debug log keeps the recovery observable when
            // an admin enables the Reading debug category, without re-introducing an
            // error-level line for an expected, recovered condition.
            _logger?.LogDebug("Recovered concurrent reading_progress insert (user={UserId}, item={ItemId}); re-applied as update", userId, itemId);
            _db.Entry(progress).State = EntityState.Detached;
            var existing = await _db.ReadingProgress
                .FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == itemId, ct);
            if (existing is null)
                throw; // row genuinely gone (e.g. reset concurrently) - surface it
            // The racer may have applied this exact mutation already.
            if (!string.IsNullOrEmpty(existing.LastMutationId) && existing.LastMutationId == mutationId)
                return UpdateProgressResult.Success(existing.Revision, alreadyApplied: true);
            ApplyUpdate(existing);
            await _db.SaveChangesAsync(ct);
            progress = existing;
        }

        return UpdateProgressResult.Success(progress.Revision, alreadyApplied: false);
    }

    /// <summary>
    /// True when a save failed on a SQLite constraint violation (error code 19),
    /// e.g. two concurrent first-writes racing on the reading_progress
    /// (UserId, ItemId) unique index.
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        => ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 };

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
    ///
    /// 1.9.0 semantics:
    /// - <paramref name="read"/> == true (manual mark-read): add the sticky mark AND
    ///   record progress Completed at the last page (when PageCount is known), so the
    ///   item reopens at page 1 via the universal open-position rule and drops out of
    ///   continue-reading — identical to finishing by reaching the last page (rule 3).
    /// - <paramref name="read"/> == false (clear mark): a deliberate full RESET — wipe
    ///   BOTH the mark and the reading position so the item returns to Unread and
    ///   reopens at page 1 (rule 1), via the shared <see cref="ResetItemsStateAsync"/>
    ///   primitive that single-item, multi-select and folder "unread" all use.
    /// </summary>
    public async Task<bool> SetItemReadAsync(
        long userId,
        long itemId,
        bool read,
        CancellationToken ct = default)
    {
        if (!await _auth.CanAccessItemAsync(userId, itemId, ct))
            return false;

        if (read)
        {
            await EnsureReadMarkTrackedAsync(userId, itemId, "manual", ct);
            await MarkProgressReadAtEndTrackedAsync(userId, itemId, ct);
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            await ResetItemsStateAsync(userId, new[] { itemId }, ct);
        }

        return true;
    }

    /// <summary>
    /// Shared reset primitive (1.9.0): removes BOTH the sticky read-mark and the
    /// reading-progress row for each of <paramref name="itemIds"/> belonging to
    /// <paramref name="userId"/>, so the items return to Unread and reopen at page 1.
    /// Used by the single-item clear, multi-select unread (which goes through the
    /// single-item path per archive), and folder unread. Returns the number of distinct
    /// items that actually had a mark or a progress row removed (an item that had
    /// neither was already unread). Commits once.
    /// </summary>
    private async Task<int> ResetItemsStateAsync(
        long userId,
        IReadOnlyCollection<long> itemIds,
        CancellationToken ct)
    {
        if (itemIds.Count == 0)
            return 0;

        var marks = await _db.ReadMarks
            .Where(m => m.UserId == userId && itemIds.Contains(m.ItemId))
            .ToListAsync(ct);
        var progress = await _db.ReadingProgress
            .Where(p => p.UserId == userId && itemIds.Contains(p.ItemId))
            .ToListAsync(ct);

        if (marks.Count == 0 && progress.Count == 0)
            return 0;

        _db.ReadMarks.RemoveRange(marks);
        _db.ReadingProgress.RemoveRange(progress);

        var affected = new HashSet<long>(marks.Select(m => m.ItemId));
        foreach (var p in progress)
            affected.Add(p.ItemId);

        await _db.SaveChangesAsync(ct);
        return affected.Count;
    }

    /// <summary>
    /// Records reading progress as Completed at the last page for a manual mark-read
    /// (rule 3), WITHOUT saving — the caller commits it. When PageCount is unknown the
    /// last page can't be computed, so this is a no-op and the read-mark alone stands
    /// (the open-position rule still reopens a marked item at page 1). Tracked alongside
    /// the read-mark so both commit atomically.
    /// </summary>
    private async Task MarkProgressReadAtEndTrackedAsync(long userId, long itemId, CancellationToken ct)
    {
        var item = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == itemId, ct);
        if (item?.PageCount is not int pageCount || pageCount <= 0)
            return;

        var lastPage = pageCount - 1;
        var now = DateTimeOffset.UtcNow;
        var progress = await _db.ReadingProgress
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == itemId, ct);

        if (progress is null)
        {
            _db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = userId,
                ItemId = itemId,
                ContentVersion = item.ContentVersion,
                EntryKey = OpaqueId.Encode(lastPage),
                Ordinal = lastPage,
                NormalizedAnchor = 0.0,
                State = (int)ReadingState.Completed,
                Revision = 1,
                LastMutationId = string.Empty,
                UpdatedAt = now,
                CompletedAt = now,
            });
        }
        else
        {
            progress.ContentVersion = item.ContentVersion;
            progress.EntryKey = OpaqueId.Encode(lastPage);
            progress.Ordinal = lastPage;
            progress.State = (int)ReadingState.Completed;
            progress.Revision++;
            progress.UpdatedAt = now;
            progress.CompletedAt ??= now;
            // Finished => no longer mid-read; the read-mark already excludes it from
            // continue-reading, but keep the dismiss flag out of the way of a later
            // genuine re-read (forward progress clears it again via UpdateProgressAsync).
            progress.HiddenFromContinue = false;
        }
    }

    /// <summary>
    /// Sets or clears read-marks in bulk across every readable descendant archive of a
    /// folder. Returns null if the node is missing, not a folder, or inaccessible.
    /// When clearing (read: false), performs the full reset of each descendant archive
    /// via the shared <see cref="ResetItemsStateAsync"/> primitive (1.9.0; folder-scale
    /// form of the single-item clear) — removing both the read-mark and reading_progress
    /// so InProgress archives return to Unread and the derived folder rollup recomputes.
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

        int affected;
        if (read)
        {
            var already = await _db.ReadMarks
                .Where(m => m.UserId == userId && archiveIds.Contains(m.ItemId))
                .Select(m => m.ItemId)
                .ToListAsync(ct);
            var alreadySet = already.ToHashSet();

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
            if (affected > 0)
                await _db.SaveChangesAsync(ct);
        }
        else
        {
            // Folder "unread" is a full reset of every descendant archive — the
            // folder-scale form of the single-item clear (rule 1). Delegated to the
            // shared ResetItemsStateAsync primitive so single-item, multi-select and
            // folder unread all produce the identical Unread-and-reopen-at-page-1 outcome
            // (removes mark + progress; descendants return to Unread and the derived
            // folder rollup recomputes). The MARK-READ path above is unchanged.
            affected = await ResetItemsStateAsync(userId, archiveIds, ct);
        }

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
    /// When <paramref name="incognito"/> is active, items in the user's Private
    /// libraries are excluded (1.4.0).
    /// </summary>
    public async Task<IReadOnlyList<ContinueReadingEntry>> GetContinueReadingAsync(
        long userId,
        int limit = 20,
        bool incognito = false,
        CancellationToken ct = default)
    {
        var visibleLibs = await _auth.GetVisibleLibraryIdsAsync(userId, incognito, ct);

        // DateTimeOffset is stored as a comparable long via
        // DateTimeOffsetToBinaryConverter (see MangaPixerDbContext.ConfigureConventions),
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
               && visibleLibs.Contains(n.LibraryId)
               && n.Availability != (int)CatalogNodeAvailability.Tombstoned
            orderby p.UpdatedAt descending
            select new ContinueReadingEntry
            {
                ItemId = n.PublicId,
                LibraryId = n.Library != null ? n.Library.PublicId : "",
                LibraryName = n.Library != null ? n.Library.DisplayName : "",
                DisplayName = n.DisplayName,
                PageIndex = p.Ordinal,
                ContentVersion = p.ContentVersion,
                UpdatedAt = p.UpdatedAt,
            })
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gets continue-reading items for a user within a single library (1.4.0 sidebar
    /// grouping). Same filters as <see cref="GetContinueReadingAsync"/> plus a
    /// library filter. When <paramref name="incognito"/> is active and the requested
    /// library is Private, returns empty (the library is hidden from listing).
    /// </summary>
    public async Task<IReadOnlyList<ContinueReadingEntry>> GetContinueReadingByLibraryAsync(
        long userId,
        long libraryId,
        int limit = 20,
        bool incognito = false,
        CancellationToken ct = default)
    {
        var visibleLibs = await _auth.GetVisibleLibraryIdsAsync(userId, incognito, ct);
        if (!visibleLibs.Contains(libraryId))
            return [];

        return await (
            from p in _db.ReadingProgress
            join n in _db.CatalogNodes on p.ItemId equals n.Id
            where p.UserId == userId
               && p.State == (int)ReadingState.InProgress
               && !p.HiddenFromContinue
               && !_db.ReadMarks.Any(m => m.UserId == userId && m.ItemId == p.ItemId)
               && n.LibraryId == libraryId
               && n.Availability != (int)CatalogNodeAvailability.Tombstoned
            orderby p.UpdatedAt descending
            select new ContinueReadingEntry
            {
                ItemId = n.PublicId,
                LibraryId = n.Library != null ? n.Library.PublicId : "",
                LibraryName = n.Library != null ? n.Library.DisplayName : "",
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
            AlwaysOpenReadFromStart = prefs.AlwaysOpenReadFromStart,
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
                AlwaysOpenReadFromStart = preferences.AlwaysOpenReadFromStart,
            };
            _db.ReaderPreferences.Add(prefs);
        }
        else
        {
            prefs.DefaultReaderMode = (int)preferences.DefaultReaderMode;
            prefs.PreferDoubleSpread = preferences.PreferDoubleSpread;
            prefs.ReducedMotion = preferences.ReducedMotion;
            prefs.PreferredBackground = preferences.PreferredBackground;
            prefs.AlwaysOpenReadFromStart = preferences.AlwaysOpenReadFromStart;
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Gets the user's library browse presentation preferences (1.2.0). Returns
    /// defaults when none are stored.
    /// </summary>
    public async Task<LibraryViewPreferencesDto> GetLibraryPreferencesAsync(
        long userId,
        CancellationToken ct = default)
    {
        var prefs = await _db.ReaderPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (prefs is null)
            return new LibraryViewPreferencesDto();

        return new LibraryViewPreferencesDto
        {
            ViewMode = prefs.LibraryViewMode,
            Density = prefs.LibraryGridDensity,
            Sort = prefs.LibrarySort,
            Direction = prefs.LibraryDirection,
            CardSize = prefs.LibraryCardSize,
            LibraryPageSize = prefs.LibraryPageSize,
            HomeRecentWindowDays = prefs.HomeRecentWindowDays,
        };
    }

    /// <summary>
    /// Sets the user's library browse presentation preferences (1.2.0). Creates the
    /// preferences row if absent, touching only the library-view columns so reader
    /// preferences are left intact.
    /// </summary>
    public async Task SetLibraryPreferencesAsync(
        long userId,
        LibraryViewPreferencesDto preferences,
        CancellationToken ct = default)
    {
        var prefs = await _db.ReaderPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (prefs is null)
        {
            prefs = new ReaderPreferencesEntity { UserId = userId };
            _db.ReaderPreferences.Add(prefs);
        }

        prefs.LibraryViewMode = preferences.ViewMode;
        prefs.LibraryGridDensity = preferences.Density;
        prefs.LibrarySort = preferences.Sort;
        prefs.LibraryDirection = preferences.Direction;
        prefs.LibraryCardSize = preferences.CardSize;
        prefs.LibraryPageSize = preferences.LibraryPageSize;
        prefs.HomeRecentWindowDays = preferences.HomeRecentWindowDays;

        await _db.SaveChangesAsync(ct);
    }

    // --- Private library designations (1.4.0) ---
    //
    // A per-(user, library) row whose presence means "Private". Private libraries
    // are hidden from listing/discovery surfaces while Incognito mode is active,
    // but direct reader URLs remain accessible. The set is replaced wholesale on
    // each PUT — the client sends the complete list of library public IDs.

    /// <summary>
    /// Gets the current user's Private library designations as public IDs.
    /// </summary>
    public async Task<PrivateLibrariesDto> GetPrivateLibrariesAsync(
        long userId,
        CancellationToken ct = default)
    {
        var publicIds = await (
            from p in _db.PrivateLibraries
            join l in _db.Libraries on p.LibraryId equals l.Id
            where p.UserId == userId
            select l.PublicId)
            .ToListAsync(ct);

        return new PrivateLibrariesDto { LibraryIds = publicIds };
    }

    /// <summary>
    /// Replaces the current user's Private library set. Libraries are identified
    /// by public ID; unknown IDs are silently skipped. The entire set is
    /// replaced on each call.
    /// </summary>
    public async Task SetPrivateLibrariesAsync(
        long userId,
        IReadOnlyList<string> libraryPublicIds,
        CancellationToken ct = default)
    {
        // Resolve public IDs to internal library IDs, skipping unknowns.
        var libIds = await _db.Libraries
            .Where(l => libraryPublicIds.Contains(l.PublicId))
            .Select(l => l.Id)
            .ToListAsync(ct);
        var libIdSet = libIds.ToHashSet();

        // Remove existing designations not in the new set.
        var existing = await _db.PrivateLibraries
            .Where(p => p.UserId == userId)
            .ToListAsync(ct);

        var toRemove = existing.Where(p => !libIdSet.Contains(p.LibraryId)).ToList();
        var existingIds = existing.Select(p => p.LibraryId).ToHashSet();
        var toAdd = libIds.Where(id => !existingIds.Contains(id)).ToList();

        if (toRemove.Count > 0)
            _db.PrivateLibraries.RemoveRange(toRemove);

        if (toAdd.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var id in toAdd)
            {
                _db.PrivateLibraries.Add(new PrivateLibraryEntity
                {
                    UserId = userId,
                    LibraryId = id,
                    MarkedAt = now,
                });
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    // --- Home library-visibility preference (1.12.0) ---
    //
    // A per-(user, library) row whose presence means "hide this library from the home
    // 'New chapters' surface". Independent of the Private designation and of Incognito.
    // The set is replaced wholesale on each PUT; only libraries the user can access may
    // be hidden (others are silently skipped).

    /// <summary>
    /// Gets the current user's home-excluded libraries as public IDs.
    /// </summary>
    public async Task<HomeLibraryVisibilityDto> GetHomeLibrariesAsync(
        long userId,
        CancellationToken ct = default)
    {
        var publicIds = await (
            from h in _db.HomeExcludedLibraries
            join l in _db.Libraries on h.LibraryId equals l.Id
            where h.UserId == userId
            select l.PublicId)
            .ToListAsync(ct);

        return new HomeLibraryVisibilityDto { ExcludedLibraryIds = publicIds };
    }

    /// <summary>
    /// Replaces the current user's home-excluded library set. Libraries are identified by
    /// public ID; ids the user cannot access (or unknown ids) are silently skipped. The
    /// entire set is replaced on each call.
    /// </summary>
    public async Task SetHomeLibrariesAsync(
        long userId,
        IReadOnlyList<string> excludedPublicIds,
        CancellationToken ct = default)
    {
        // Validate: only libraries in the user's accessible set may be hidden.
        var accessible = (await _auth.GetAccessibleLibraryIdsAsync(userId, ct)).ToHashSet();
        var libIds = await _db.Libraries
            .Where(l => excludedPublicIds.Contains(l.PublicId))
            .Select(l => l.Id)
            .ToListAsync(ct);
        var keep = libIds.Where(accessible.Contains).ToList();
        var keepSet = keep.ToHashSet();

        var existing = await _db.HomeExcludedLibraries
            .Where(h => h.UserId == userId)
            .ToListAsync(ct);

        var toRemove = existing.Where(h => !keepSet.Contains(h.LibraryId)).ToList();
        var existingIds = existing.Select(h => h.LibraryId).ToHashSet();
        var toAdd = keep.Where(id => !existingIds.Contains(id)).ToList();

        if (toRemove.Count > 0)
            _db.HomeExcludedLibraries.RemoveRange(toRemove);

        if (toAdd.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var id in toAdd)
            {
                _db.HomeExcludedLibraries.Add(new HomeExcludedLibraryEntity
                {
                    UserId = userId,
                    LibraryId = id,
                    MarkedAt = now,
                });
            }
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

    /// <summary>
    /// Opaque public ID of the item's library (1.4.0). Enables sidebar
    /// grouping by library without a second round-trip.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    /// Display name of the item's library (1.4.0).
    /// </summary>
    public required string LibraryName { get; init; }

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
