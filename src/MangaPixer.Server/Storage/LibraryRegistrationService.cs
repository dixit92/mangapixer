namespace com.lifepixer.mangapixer.Server.Storage;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Library registration service. Validates and registers library roots.
/// Enforces:
/// - Canonical source/app-root separation (library roots cannot be inside app-data roots)
/// - Duplicate root rejection (same canonical path cannot be registered twice)
/// - Nested root rejection (a root cannot be inside another root, or vice versa)
/// - Root accessibility validation
/// - Case-comparison policy per library
/// </summary>
public sealed class LibraryRegistrationService
{
    private readonly MangaPixerDbContext _db;
    private readonly AppRootOptions _appRootOptions;
    private readonly ThumbnailStore? _thumbnailStore;
    private readonly ILogger<LibraryRegistrationService>? _logger;

    public LibraryRegistrationService(
        MangaPixerDbContext db,
        AppRootOptions? appRootOptions = null,
        ThumbnailStore? thumbnailStore = null,
        ILogger<LibraryRegistrationService>? logger = null)
    {
        _db = db;
        _appRootOptions = appRootOptions ?? new AppRootOptions();
        _thumbnailStore = thumbnailStore;
        _logger = logger;
    }

    /// <summary>
    /// Registers a new library root. Validates the root and checks for duplicates/nesting.
    /// </summary>
    public async Task<LibraryRegistrationResult> RegisterAsync(
        string displayName,
        string rootPath,
        string caseComparisonPolicy = "ordinal",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return LibraryRegistrationResult.Fail("invalid_display_name", "Display name is required.");

        if (string.IsNullOrWhiteSpace(rootPath))
            return LibraryRegistrationResult.Fail("invalid_root_path", "Root path is required.");

        // Canonicalize the path
        var canonicalPath = CanonicalizePath(rootPath);
        if (canonicalPath is null)
            return LibraryRegistrationResult.Fail("invalid_root_path", "Root path could not be canonicalized.");

        // Check app-root separation: library root must not be inside app-data
        if (IsInsideAppRoot(canonicalPath))
            return LibraryRegistrationResult.Fail("app_root_conflict", "Library root cannot be inside application data root.");

        // Check if app-data is inside the library root (reverse conflict)
        if (IsAppRootInside(canonicalPath))
            return LibraryRegistrationResult.Fail("app_root_conflict", "Application data root cannot be inside library root.");

        // Check for duplicate roots
        var existingLibraries = await _db.Libraries.ToListAsync(ct);
        foreach (var existing in existingLibraries)
        {
            var existingCanonical = CanonicalizePath(existing.RootPath);
            if (existingCanonical is null) continue;

            var cmp = StringComparer.OrdinalIgnoreCase;
            if (cmp.Equals(existingCanonical, canonicalPath))
                return LibraryRegistrationResult.Fail("duplicate_root", "A library with this root path already exists.");

            // Check nesting: new root inside existing root
            if (IsNested(canonicalPath, existingCanonical))
                return LibraryRegistrationResult.Fail("nested_root", "Library root is inside an existing library root.");

            // Check nesting: existing root inside new root
            if (IsNested(existingCanonical, canonicalPath))
                return LibraryRegistrationResult.Fail("nested_root", "Library root contains an existing library root.");
        }

        // Validate root accessibility
        if (!Directory.Exists(canonicalPath))
            return LibraryRegistrationResult.Fail("root_not_found", "Library root directory does not exist.");

        // Get root identity
        var fs = new ReadOnlyLibraryFileSystem(canonicalPath);
        var rootIdentity = fs.GetRootIdentity();

        // Create the library entity
        var library = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
            DisplayName = displayName,
            RootPath = canonicalPath,
            CaseComparisonPolicy = caseComparisonPolicy,
            RootIdentity = rootIdentity,
            State = "active",
            CatalogRevision = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.Libraries.Add(library);
        await _db.SaveChangesAsync(ct);

        return LibraryRegistrationResult.Ok(library);
    }

    /// <summary>
    /// Deletes a library and ALL of its MangaPixer metadata, leaving the source
    /// files on disk untouched (source-media read-only invariant). This is a
    /// metadata-only delete — the opposite of <see cref="RegisterAsync"/>, which
    /// only ever wrote metadata.
    ///
    /// Cleanup order (owner plan, 2026-09-09):
    /// 1. Resolve the library's archive-item node ids + thumbnail content
    ///    versions BEFORE any rows are removed (needed afterwards).
    /// 2. Delete the durable thumbnail files for those items from
    ///    <c>DataRoot/thumbnails</c> (filesystem I/O — done OUTSIDE the write
    ///    transaction per the DbContext concurrency rules).
    /// 3. Inside a single transaction, delete loose-reference rows that have NO
    ///    FK cascade (keyed by ItemId or LibraryId), the FTS <c>catalog_search</c>
    ///    rows for the library (cascade deletes do not fire FTS triggers because
    ///    <c>PRAGMA recursive_triggers</c> is OFF), then remove the library row
    ///    whose EF cascade removes <c>catalog_nodes</c> → <c>archive_items</c> →
    ///    <c>page_entries</c> and <c>folder_reader_defaults</c>.
    ///
    /// Returns false if the library does not exist. Logs counts only (no paths).
    /// </summary>
    public async Task<bool> DeleteAsync(long libraryId, CancellationToken ct = default)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.Id == libraryId, ct);
        if (library is null)
            return false;

        // 1. Resolve archive-item node ids (Kind == 1) and their thumbnail
        //    content versions before any rows are removed.
        var itemIds = await _db.CatalogNodes
            .Where(n => n.LibraryId == libraryId && n.Kind == 1)
            .Select(n => n.Id)
            .ToListAsync(ct);

        var thumbnailKeys = itemIds.Count == 0
            ? []
            : await _db.ArchiveItems
                .Where(a => itemIds.Contains(a.NodeId) && a.ThumbnailContentVersion != null)
                .Select(a => new { a.NodeId, ContentVersion = a.ThumbnailContentVersion!.Value })
                .ToListAsync(ct);

        // 2. Delete durable thumbnail files (filesystem I/O, outside the tx).
        var thumbnailsDeleted = 0;
        if (_thumbnailStore is not null)
        {
            foreach (var key in thumbnailKeys)
            {
                _thumbnailStore.Delete(key.NodeId, key.ContentVersion);
                thumbnailsDeleted++;
            }
        }

        // 3. Transactional metadata cleanup. ExecuteDeleteAsync participates in
        //    the ambient transaction. The library-row removal at the end relies on
        //    EF cascade for the FK-backed tables; the loose-reference + FTS rows
        //    are removed explicitly because they have no FK cascade.
        using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Defer FK checks to transaction commit. The catalog_nodes self-FK
        // (ParentId → Id) is ON DELETE RESTRICT, so cascading a library delete
        // through parent/child nodes would otherwise fail mid-statement when a
        // parent is removed before its child. Deferring makes the check run at
        // commit, by which point every node for the library is gone. (The naive
        // delete that previously lived here was broken for any library with
        // nested nodes — this fixes it.)
        await _db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON;", ct);

        // FTS catalog_search: cascade deletes from catalog_nodes do not fire the
        // AFTER-DELETE trigger (recursive_triggers is OFF), so remove the
        // library's search rows explicitly by library_id.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM catalog_search WHERE library_id = {libraryId}", ct);

        // Loose references keyed by ItemId (no FK to catalog_nodes).
        var progressDeleted = 0;
        var readMarksDeleted = 0;
        var bookmarksDeleted = 0;
        var overridesDeleted = 0;
        if (itemIds.Count > 0)
        {
            progressDeleted = await _db.ReadingProgress
                .Where(p => itemIds.Contains(p.ItemId)).ExecuteDeleteAsync(ct);
            readMarksDeleted = await _db.ReadMarks
                .Where(m => itemIds.Contains(m.ItemId)).ExecuteDeleteAsync(ct);
            bookmarksDeleted = await _db.Bookmarks
                .Where(b => itemIds.Contains(b.ItemId)).ExecuteDeleteAsync(ct);
            overridesDeleted = await _db.ItemReaderOverrides
                .Where(o => itemIds.Contains(o.ItemId)).ExecuteDeleteAsync(ct);
        }

        // Loose references keyed by LibraryId (no FK to libraries).
        var scanRunsDeleted = await _db.ScanRuns
            .Where(s => s.LibraryId == libraryId).ExecuteDeleteAsync(ct);
        var scanObservationsDeleted = await _db.ScanObservations
            .Where(o => o.LibraryId == libraryId).ExecuteDeleteAsync(ct);
        var grantsDeleted = await _db.LibraryGrants
            .Where(g => g.LibraryId == libraryId).ExecuteDeleteAsync(ct);
        var jobsDeleted = await _db.Jobs
            .Where(j => j.LibraryId == libraryId).ExecuteDeleteAsync(ct);

        // Remove the library row — EF cascade handles catalog_nodes (→
        // archive_items → page_entries) and folder_reader_defaults.
        _db.Libraries.Remove(library);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger?.LogInformation(
            LogEvents.Administration.LibraryDeleted,
            "Deleted library {LibraryId}: {Items} items, {Thumbnails} thumbnails, " +
            "{Progress} progress, {ReadMarks} read marks, {Bookmarks} bookmarks, " +
            "{Overrides} overrides, {ScanRuns} scan runs, {ScanObservations} scan observations, " +
            "{Grants} grants, {Jobs} jobs",
            libraryId, itemIds.Count, thumbnailsDeleted,
            progressDeleted, readMarksDeleted, bookmarksDeleted, overridesDeleted,
            scanRunsDeleted, scanObservationsDeleted, grantsDeleted, jobsDeleted);

        return true;
    }

    private static string? CanonicalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private bool IsInsideAppRoot(string libraryPath)
    {
        foreach (var appRoot in new[] { _appRootOptions.DataRoot, _appRootOptions.CacheRoot, _appRootOptions.ScratchRoot })
        {
            if (string.IsNullOrEmpty(appRoot)) continue;
            var canonical = CanonicalizePath(appRoot);
            if (canonical is null) continue;
            // Library path is the app root or inside it
            if (string.Equals(libraryPath, canonical, StringComparison.OrdinalIgnoreCase)) return true;
            if (IsNested(libraryPath, canonical))
                return true;
        }
        return false;
    }

    private bool IsAppRootInside(string libraryPath)
    {
        foreach (var appRoot in new[] { _appRootOptions.DataRoot, _appRootOptions.CacheRoot, _appRootOptions.ScratchRoot })
        {
            if (string.IsNullOrEmpty(appRoot)) continue;
            var canonical = CanonicalizePath(appRoot);
            if (canonical is null) continue;
            // App root is the library path or inside it
            if (string.Equals(libraryPath, canonical, StringComparison.OrdinalIgnoreCase)) return true;
            if (IsNested(canonical, libraryPath))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="child"/> is inside <paramref name="parent"/>.
    /// Uses ordinal-ignore-case comparison for path nesting.
    /// </summary>
    private static bool IsNested(string child, string parent)
    {
        var parentWithSep = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(parentWithSep, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Result of a library registration attempt.
/// </summary>
public sealed class LibraryRegistrationResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public LibraryEntity? Library { get; init; }

    public static LibraryRegistrationResult Ok(LibraryEntity library) => new()
    {
        Success = true,
        Library = library,
    };

    public static LibraryRegistrationResult Fail(string error, string message) => new()
    {
        Success = false,
        Error = error,
        Message = message,
    };
}

/// <summary>
/// Application root paths for separation validation.
/// </summary>
public sealed class AppRootOptions
{
    public string? DataRoot { get; set; }
    public string? CacheRoot { get; set; }
    public string? ScratchRoot { get; set; }
}
