namespace com.lifepixer.mangapixer.Server.Features.Analytics;

using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Admin-only analytics read surface (1.22.0 lane E). Aggregates library,
/// content, processing and engagement numbers (reusing
/// <see cref="DiagnosticsService"/> where it overlaps) plus a per-user
/// engagement table.
///
/// Privacy invariant: every number here is a count or a timestamp. No
/// titles, item names, archive entry names, or paths are read or returned
/// (AGENTS.md privacy invariants).
///
/// Private-library exclusion (owner decision 2026-09-22, privacy-
/// conservative default): a user's own reading activity (progress,
/// bookmarks, favorites) in a library THEY marked Private
/// (<see cref="com.lifepixer.mangapixer.Server.Persistence.Entities.PrivateLibraryEntity"/>)
/// is excluded from that user's per-user counts, even though this is an
/// admin-only surface and the admin could technically see it. This is an open
/// question for the product owner — see the feature note's Dashboard v1
/// section — but until decided otherwise, a Private designation should mean
/// "not surfaced," full stop, including here.
/// </summary>
public sealed class AnalyticsService
{
    private readonly MangaPixerDbContext _db;
    private readonly DiagnosticsService _diagnostics;

    public AnalyticsService(MangaPixerDbContext db, DiagnosticsService diagnostics)
    {
        _db = db;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Instance-wide overview: library/content/processing counts (reused from
    /// <see cref="DiagnosticsService"/>) plus engagement counts not covered by
    /// the diagnostics snapshot (completed/in-progress split, favorites,
    /// active sessions).
    /// </summary>
    public async Task<AnalyticsOverviewDto> GetOverviewAsync(CancellationToken ct = default)
    {
        var snapshot = await _diagnostics.GetSnapshotAsync(ct);

        var pendingActivationCount = await _db.Users.CountAsync(u => u.IsPendingActivation, ct);
        var completedItemCount = await _db.ReadingProgress.CountAsync(p => p.State == 2, ct);
        var inProgressItemCount = await _db.ReadingProgress.CountAsync(p => p.State == 1, ct);
        var favoriteCount = await _db.Favorites.CountAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var activeSessionCount = await _db.Sessions
            .CountAsync(s => !s.IsRevoked && s.ExpiresAt > now, ct);

        return new AnalyticsOverviewDto
        {
            GeneratedAt = now,
            LibraryCount = snapshot.Database.LibraryCount,
            TotalNodeCount = snapshot.Database.TotalNodeCount,
            ArchiveNodeCount = snapshot.Database.ArchiveNodeCount,
            FolderNodeCount = snapshot.Database.FolderNodeCount,
            TombstonedNodeCount = snapshot.Database.TombstonedNodeCount,
            AnalyzedItemCount = snapshot.Database.AnalyzedItemCount,
            PendingItemCount = snapshot.Database.PendingItemCount,
            FailedItemCount = snapshot.Database.FailedItemCount,
            UserCount = snapshot.Database.UserCount,
            ActiveUserCount = snapshot.Database.ActiveUserCount,
            AdminCount = snapshot.Database.AdminCount,
            PendingActivationCount = pendingActivationCount,
            ReadingProgressCount = snapshot.Database.ReadingProgressCount,
            CompletedItemCount = completedItemCount,
            InProgressItemCount = inProgressItemCount,
            BookmarkCount = snapshot.Database.BookmarkCount,
            FavoriteCount = favoriteCount,
            ActiveSessionCount = activeSessionCount,
        };
    }

    /// <summary>
    /// Per-user engagement table (the admin's own row included — analytics are
    /// admin-only, but an admin can see everyone including themselves).
    /// Counts are aggregated per (user, library) in SQL first, then the rows
    /// for libraries the user has privately marked are dropped client-side
    /// before summing — this keeps the query itself simple/translatable while
    /// the exclusion set (typically tiny) is cheap to apply in memory.
    /// </summary>
    public async Task<IReadOnlyList<AnalyticsUserRowDto>> GetUserAnalyticsAsync(CancellationToken ct = default)
    {
        var users = await _db.Users
            .OrderBy(u => u.UserName)
            .Select(u => new { u.Id, u.PublicId, u.UserName, u.IsAdmin, u.IsActive, u.IsPendingActivation, u.LastLoginAt })
            .ToListAsync(ct);

        var privateLibraries = await _db.PrivateLibraries
            .Select(p => new { p.UserId, p.LibraryId })
            .ToListAsync(ct);
        var privateByUser = privateLibraries
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.LibraryId).ToHashSet());

        var progressGroups = await _db.ReadingProgress
            .Join(_db.CatalogNodes, p => p.ItemId, n => n.Id, (p, n) => new { p.UserId, n.LibraryId, p.State, p.UpdatedAt })
            .GroupBy(x => new { x.UserId, x.LibraryId, x.State })
            .Select(g => new { g.Key.UserId, g.Key.LibraryId, g.Key.State, Count = g.Count(), LastActivity = g.Max(x => x.UpdatedAt) })
            .ToListAsync(ct);

        var bookmarkGroups = await _db.Bookmarks
            .Join(_db.CatalogNodes, b => b.ItemId, n => n.Id, (b, n) => new { b.UserId, n.LibraryId })
            .GroupBy(x => new { x.UserId, x.LibraryId })
            .Select(g => new { g.Key.UserId, g.Key.LibraryId, Count = g.Count() })
            .ToListAsync(ct);

        var favoriteGroups = await _db.Favorites
            .Join(_db.CatalogNodes, f => f.CatalogNodeId, n => n.Id, (f, n) => new { f.UserId, n.LibraryId })
            .GroupBy(x => new { x.UserId, x.LibraryId })
            .Select(g => new { g.Key.UserId, g.Key.LibraryId, Count = g.Count() })
            .ToListAsync(ct);

        HashSet<long> PrivateSetFor(long userId) =>
            privateByUser.TryGetValue(userId, out var set) ? set : [];

        var rows = new List<AnalyticsUserRowDto>(users.Count);
        foreach (var user in users)
        {
            var privateLibraryIds = PrivateSetFor(user.Id);

            var visibleProgress = progressGroups
                .Where(g => g.UserId == user.Id && !privateLibraryIds.Contains(g.LibraryId))
                .ToList();
            var completed = visibleProgress.Where(g => g.State == 2).Sum(g => g.Count);
            var inProgress = visibleProgress.Where(g => g.State == 1).Sum(g => g.Count);
            DateTimeOffset? lastActivity = visibleProgress.Count == 0
                ? null
                : visibleProgress.Max(g => g.LastActivity);

            var bookmarkCount = bookmarkGroups
                .Where(g => g.UserId == user.Id && !privateLibraryIds.Contains(g.LibraryId))
                .Sum(g => g.Count);

            var favoriteCount = favoriteGroups
                .Where(g => g.UserId == user.Id && !privateLibraryIds.Contains(g.LibraryId))
                .Sum(g => g.Count);

            rows.Add(new AnalyticsUserRowDto
            {
                Id = user.PublicId,
                Username = user.UserName,
                IsAdmin = user.IsAdmin,
                IsActive = user.IsActive,
                IsPendingActivation = user.IsPendingActivation,
                LastLoginAt = user.LastLoginAt,
                ChaptersCompleted = completed,
                ChaptersInProgress = inProgress,
                BookmarkCount = bookmarkCount,
                FavoriteCount = favoriteCount,
                LastReadingActivityAt = lastActivity,
            });
        }

        return rows;
    }
}

/// <summary>
/// Instance-wide analytics overview — counts and a generation timestamp only.
/// </summary>
public sealed record AnalyticsOverviewDto
{
    public required DateTimeOffset GeneratedAt { get; init; }

    public required int LibraryCount { get; init; }
    public required int TotalNodeCount { get; init; }
    public required int ArchiveNodeCount { get; init; }
    public required int FolderNodeCount { get; init; }
    public required int TombstonedNodeCount { get; init; }
    public required int AnalyzedItemCount { get; init; }
    public required int PendingItemCount { get; init; }
    public required int FailedItemCount { get; init; }

    public required int UserCount { get; init; }
    public required int ActiveUserCount { get; init; }
    public required int AdminCount { get; init; }
    public required int PendingActivationCount { get; init; }

    public required int ReadingProgressCount { get; init; }
    public required int CompletedItemCount { get; init; }
    public required int InProgressItemCount { get; init; }
    public required int BookmarkCount { get; init; }
    public required int FavoriteCount { get; init; }
    public required int ActiveSessionCount { get; init; }
}

/// <summary>
/// One row of the per-user analytics table. Counts and timestamps only —
/// never item names or paths. Reading activity in libraries the user has
/// marked Private is excluded (see the exclusion note on
/// <see cref="AnalyticsService"/>).
/// </summary>
public sealed record AnalyticsUserRowDto
{
    public required string Id { get; init; }
    public required string Username { get; init; }
    public required bool IsAdmin { get; init; }
    public required bool IsActive { get; init; }
    public required bool IsPendingActivation { get; init; }
    public required DateTimeOffset? LastLoginAt { get; init; }

    public required int ChaptersCompleted { get; init; }
    public required int ChaptersInProgress { get; init; }
    public required int BookmarkCount { get; init; }
    public required int FavoriteCount { get; init; }
    public required DateTimeOffset? LastReadingActivityAt { get; init; }
}
