namespace com.lifepixer.mangaplex.Server.Operations;

using com.lifepixer.mangaplex.Server.Logging;
using com.lifepixer.mangaplex.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

/// <summary>
/// Diagnostics service. Provides structured diagnostic exports for troubleshooting.
///
/// Rules:
/// - Logs contain IDs, counts, timings, and sanitized error codes only.
/// - Never expose absolute paths, titles, archive entry names, passwords, tokens.
/// - Diagnostic export is admin-only.
/// - No stack traces or source paths in error responses.
/// - Canary redaction: test entries are redacted in production.
/// </summary>
public sealed class DiagnosticsService
{
    private readonly MangaPlexDbContext _db;
    private readonly ILogger<DiagnosticsService>? _logger;

    public DiagnosticsService(MangaPlexDbContext db, ILogger<DiagnosticsService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Exports a diagnostic snapshot (counts, states, timings — no private data).
    /// </summary>
    public async Task<DiagnosticsSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var libraryCount = await _db.Libraries.CountAsync(ct);
        var userCount = await _db.Users.CountAsync(ct);
        var activeUserCount = await _db.Users.CountAsync(u => u.IsActive, ct);
        var adminCount = await _db.Users.CountAsync(u => u.IsAdmin && u.IsActive, ct);
        var nodeCount = await _db.CatalogNodes.CountAsync(ct);
        var archiveCount = await _db.CatalogNodes.CountAsync(n => n.Kind == 1, ct);
        var folderCount = await _db.CatalogNodes.CountAsync(n => n.Kind == 0, ct);
        var tombstonedCount = await _db.CatalogNodes.CountAsync(n => n.Availability == 5, ct);
        var analyzedItems = await _db.ArchiveItems.CountAsync(a => a.AnalysisState == 0, ct);
        var pendingItems = await _db.ArchiveItems.CountAsync(a => a.AnalysisState == 1, ct);
        var failedItems = await _db.ArchiveItems.CountAsync(a => a.AnalysisState == 2, ct);
        var progressCount = await _db.ReadingProgress.CountAsync(ct);
        var bookmarkCount = await _db.Bookmarks.CountAsync(ct);
        var sessionCount = await _db.Sessions.CountAsync(ct);
        var grantCount = await _db.LibraryGrants.CountAsync(ct);

        var process = Process.GetCurrentProcess();

        return new DiagnosticsSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            Database = new DatabaseDiagnostics
            {
                LibraryCount = libraryCount,
                UserCount = userCount,
                ActiveUserCount = activeUserCount,
                AdminCount = adminCount,
                TotalNodeCount = nodeCount,
                ArchiveNodeCount = archiveCount,
                FolderNodeCount = folderCount,
                TombstonedNodeCount = tombstonedCount,
                AnalyzedItemCount = analyzedItems,
                PendingItemCount = pendingItems,
                FailedItemCount = failedItems,
                ReadingProgressCount = progressCount,
                BookmarkCount = bookmarkCount,
                SessionCount = sessionCount,
                LibraryGrantCount = grantCount,
            },
            Process = new ProcessDiagnostics
            {
                ProcessId = process.Id,
                WorkingSetBytes = process.WorkingSet64,
                UptimeSeconds = (long)(DateTimeOffset.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds,
                ThreadCount = process.Threads.Count,
            },
        };
    }

    /// <summary>
    /// Exports a sanitized diagnostic log (no paths, titles, or sensitive data).
    /// </summary>
    public async Task<SanitizedLogExport> ExportLogAsync(CancellationToken ct = default)
    {
        // Return a sanitized summary — never raw log lines with paths
        var snapshot = await GetSnapshotAsync(ct);

        return new SanitizedLogExport
        {
            Snapshot = snapshot,
            // Category names derived from the real event-ID ranges (gap 8.3.10) —
            // no message content that might contain paths
            EventCategories = LogEvents.CategoryRanges.Select(r => r.Name).ToArray(),
        };
    }
}

/// <summary>
/// Diagnostic snapshot — counts and states only, no private data.
/// </summary>
public sealed record DiagnosticsSnapshot
{
    public required DateTimeOffset Timestamp { get; init; }
    public required DatabaseDiagnostics Database { get; init; }
    public required ProcessDiagnostics Process { get; init; }
}

/// <summary>
/// Database diagnostic counts.
/// </summary>
public sealed record DatabaseDiagnostics
{
    public required int LibraryCount { get; init; }
    public required int UserCount { get; init; }
    public required int ActiveUserCount { get; init; }
    public required int AdminCount { get; init; }
    public required int TotalNodeCount { get; init; }
    public required int ArchiveNodeCount { get; init; }
    public required int FolderNodeCount { get; init; }
    public required int TombstonedNodeCount { get; init; }
    public required int AnalyzedItemCount { get; init; }
    public required int PendingItemCount { get; init; }
    public required int FailedItemCount { get; init; }
    public required int ReadingProgressCount { get; init; }
    public required int BookmarkCount { get; init; }
    public required int SessionCount { get; init; }
    public required int LibraryGrantCount { get; init; }
}

/// <summary>
/// Process diagnostic info.
/// </summary>
public sealed record ProcessDiagnostics
{
    public required int ProcessId { get; init; }
    public required long WorkingSetBytes { get; init; }
    public required long UptimeSeconds { get; init; }
    public required int ThreadCount { get; init; }
}

/// <summary>
/// Sanitized log export — no paths, titles, or sensitive data.
/// </summary>
public sealed record SanitizedLogExport
{
    public required DiagnosticsSnapshot Snapshot { get; init; }
    public required string[] EventCategories { get; init; }
}
