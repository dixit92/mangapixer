namespace com.lifepixer.mangaplex.Server.Storage;

using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

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
    private readonly MangaPlexDbContext _db;
    private readonly AppRootOptions _appRootOptions;

    public LibraryRegistrationService(MangaPlexDbContext db, AppRootOptions? appRootOptions = null)
    {
        _db = db;
        _appRootOptions = appRootOptions ?? new AppRootOptions();
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
    /// Unregisters a library. Does not delete any files — only removes the database record.
    /// </summary>
    public async Task<bool> UnregisterAsync(long libraryId, CancellationToken ct = default)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.Id == libraryId, ct);
        if (library is null)
            return false;

        _db.Libraries.Remove(library);
        await _db.SaveChangesAsync(ct);
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
