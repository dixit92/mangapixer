namespace com.lifepixer.mangaplex.Server.Media;

using com.lifepixer.mangaplex.Server.Logging;

using System.IO;
using System.Security.Cryptography;

/// <summary>
/// Manages per-attempt scratch workspaces below ScratchRoot.
/// Each job attempt gets a separate opaque workspace with a server-generated
/// random name. Paths are never derived from archive entry names.
///
/// Safety rules:
/// - Workspaces are created below ScratchRoot only.
/// - Crash recovery removes only verifiably owned, inactive workspaces.
/// - Never recursively delete unfamiliar directories or follow links.
/// - Failed cleanup counts against scratch budget and is retried safely.
/// </summary>
public sealed class ScratchWorkspaceManager
{
    private readonly string _scratchRoot;
    private readonly long _scratchBudgetBytes;
    private readonly string _ownershipMarker;
    private readonly ILogger<ScratchWorkspaceManager>? _logger;

    /// <summary>
    /// The ownership marker file name placed in each workspace to identify
    /// it as application-owned. Used during crash recovery to distinguish
    /// app workspaces from unrelated directories.
    /// </summary>
    public const string OwnershipMarkerFileName = ".mangaplex-scratch";

    public ScratchWorkspaceManager(string scratchRoot, long scratchBudgetBytes = 2L * 1024 * 1024 * 1024, ILogger<ScratchWorkspaceManager>? logger = null)
    {
        _scratchRoot = Path.GetFullPath(scratchRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _scratchBudgetBytes = scratchBudgetBytes;
        _ownershipMarker = Path.Combine(_scratchRoot, ".mangaplex-root");
        _logger = logger;
    }

    /// <summary>
    /// Initializes the scratch root directory and writes the ownership marker.
    /// Called once at server startup.
    /// </summary>
    public void Initialize()
    {
        Directory.CreateDirectory(_scratchRoot);
        if (!File.Exists(_ownershipMarker))
        {
            File.WriteAllText(_ownershipMarker,
                $"MangaPlex scratch root\nCreated: {DateTimeOffset.UtcNow:O}\n");
        }
    }

    /// <summary>
    /// Allocates a new opaque workspace for a job attempt.
    /// Returns the workspace path. The workspace is created with an ownership marker.
    /// </summary>
    public ScratchWorkspace AllocateWorkspace()
    {
        // Generate a random opaque name — never derive from archive entry names
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var workspacePath = Path.Combine(_scratchRoot, id);

        Directory.CreateDirectory(workspacePath);

        // Write ownership marker
        var markerPath = Path.Combine(workspacePath, OwnershipMarkerFileName);
        File.WriteAllText(markerPath,
            $"MangaPlex scratch workspace\nId: {id}\nCreated: {DateTimeOffset.UtcNow:O}\n");

        // Per-allocation receipt is Trace-level: it fires once per job and the
        // byte count of a fresh workspace is not actionable.
        _logger?.LogTrace(LogEvents.Worker.ScratchWorkspaceAllocated,
            "Scratch workspace {WorkspaceId} allocated (usage {Usage} of {Budget} bytes)",
            id, GetCurrentUsageBytes(), _scratchBudgetBytes);

        return new ScratchWorkspace
        {
            Id = id,
            Path = workspacePath,
            Manager = this,
        };
    }

    /// <summary>
    /// Validates that a path is contained within the given workspace.
    /// Returns false if the path escapes the workspace (path traversal protection).
    /// </summary>
    public bool IsPathContainedIn(string workspacePath, string testPath)
    {
        var normalizedWorkspace = Path.GetFullPath(workspacePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTest = Path.GetFullPath(testPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedWorkspace, normalizedTest, StringComparison.Ordinal))
            return true;

        var workspaceWithSep = normalizedWorkspace + Path.DirectorySeparatorChar;
        return normalizedTest.StartsWith(workspaceWithSep, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cleans up a workspace after job completion (success or failure).
    /// Removes only the specific workspace directory — never recursive
    /// deletion of unfamiliar directories.
    /// </summary>
    public void CleanupWorkspace(string workspacePath)
    {
        if (!IsOwnedWorkspace(workspacePath))
        {
            _logger?.LogDebug(LogEvents.Worker.ScratchCleanupSkippedUnowned, "Scratch cleanup skipped: path is not an owned workspace");
            return;
        }

        try
        {
            if (Directory.Exists(workspacePath))
                Directory.Delete(workspacePath, recursive: true);
        }
        catch (Exception ex)
        {
            // Failed cleanup counts against scratch budget.
            // It will be retried during crash recovery.
            _logger?.LogWarning(LogEvents.Worker.ScratchCleanupFailed, ex, "Scratch workspace cleanup failed (will retry during recovery): {Error}", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Crash recovery: removes only verifiably owned, inactive workspaces.
    /// A workspace is "owned" if it contains the ownership marker file.
    /// A workspace is "inactive" if it has no recently modified files (heuristic).
    /// Never follows symlinks or deletes unfamiliar directories.
    /// </summary>
    public int RecoverInactiveWorkspaces(TimeSpan inactiveThreshold)
    {
        if (!Directory.Exists(_scratchRoot))
            return 0;

        var now = DateTimeOffset.UtcNow;
        var cleaned = 0;

        foreach (var dir in Directory.EnumerateDirectories(_scratchRoot))
        {
            // Skip if not an owned workspace
            var markerPath = Path.Combine(dir, OwnershipMarkerFileName);
            if (!File.Exists(markerPath))
                continue;

            // Check if the workspace is inactive
            var lastWrite = Directory.GetLastWriteTimeUtc(dir);
            if (now.UtcDateTime - lastWrite < inactiveThreshold)
                continue;

            // Safe to clean up — it's owned and inactive
            try
            {
                Directory.Delete(dir, recursive: true);
                cleaned++;
            }
            catch (Exception ex)
            {
                // Failed cleanup — will be retried next recovery cycle
                _logger?.LogWarning(LogEvents.Worker.ScratchRecoveryCleanupFailed, ex, "Scratch recovery cleanup failed for one workspace: {Error}", ex.GetType().Name);
            }
        }

        _logger?.LogDebug(LogEvents.Worker.ScratchRecoveryPassComplete, "Scratch recovery pass complete: {Cleaned} workspace(s) removed", cleaned);
        return cleaned;
    }

    /// <summary>
    /// Returns the current total size of all owned workspaces in bytes.
    /// Used for quota tracking.
    /// </summary>
    public long GetCurrentUsageBytes()
    {
        if (!Directory.Exists(_scratchRoot))
            return 0;

        long total = 0;
        foreach (var dir in Directory.EnumerateDirectories(_scratchRoot))
        {
            var markerPath = Path.Combine(dir, OwnershipMarkerFileName);
            if (!File.Exists(markerPath))
                continue;

            total += GetDirectorySize(dir);
        }
        return total;
    }

    /// <summary>
    /// Returns true if there is enough scratch budget remaining for a new workspace.
    /// </summary>
    public bool HasAvailableBudget(long estimatedBytes)
    {
        return GetCurrentUsageBytes() + estimatedBytes <= _scratchBudgetBytes;
    }

    /// <summary>
    /// Returns true if the given path is an owned workspace (contains the ownership marker).
    /// </summary>
    public bool IsOwnedWorkspace(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var markerPath = Path.Combine(path, OwnershipMarkerFileName);
        return File.Exists(markerPath);
    }

    /// <summary>
    /// The scratch root directory.
    /// </summary>
    public string ScratchRoot => _scratchRoot;

    /// <summary>
    /// The total scratch budget in bytes.
    /// </summary>
    public long ScratchBudgetBytes => _scratchBudgetBytes;

    private static long GetDirectorySize(string path)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    // Don't follow symlinks
                    if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                        size += info.Length;
                }
                catch { /* skip inaccessible files */ }
            }
        }
        catch { /* skip inaccessible directories */ }
        return size;
    }
}

/// <summary>
/// Represents an allocated scratch workspace for a single job attempt.
/// Must be disposed to clean up the workspace.
/// </summary>
public sealed class ScratchWorkspace : IDisposable
{
    /// <summary>
    /// Opaque workspace ID (server-generated random).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Absolute path to the workspace directory.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Reference to the manager for cleanup.
    /// </summary>
    public required ScratchWorkspaceManager Manager { get; init; }

    public void Dispose()
    {
        Manager.CleanupWorkspace(Path);
    }
}
