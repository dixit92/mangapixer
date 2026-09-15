namespace com.lifepixer.mangaplex.Tests.Server.Media;

using com.lifepixer.mangaplex.Server.Media;
using com.lifepixer.mangaplex.TestSupport;
using Xunit;

/// <summary>
/// Integration tests for ScratchWorkspaceManager.
/// Verifies workspace allocation, containment validation, cleanup, and crash recovery.
/// </summary>
public sealed class ScratchWorkspaceTests : IDisposable
{
    private readonly string _tempDir;

    public ScratchWorkspaceTests()
    {
        _tempDir = TestSupport.CreateTempTestRoot("mangaplex-scratch");
    }

    public void Dispose()
    {
        TestSupport.CleanupDirectory(_tempDir);
    }

    [Fact]
    public void Initialize_CreatesScratchRootAndOwnershipMarker()
    {
        var scratchRoot = Path.Combine(_tempDir, "scratch");
        var manager = new ScratchWorkspaceManager(scratchRoot);

        manager.Initialize();

        Assert.True(Directory.Exists(scratchRoot));
        Assert.True(File.Exists(Path.Combine(scratchRoot, ".mangaplex-root")));
    }

    [Fact]
    public void AllocateWorkspace_CreatesDirectoryWithOwnershipMarker()
    {
        var scratchRoot = Path.Combine(_tempDir, "scratch");
        var manager = new ScratchWorkspaceManager(scratchRoot);
        manager.Initialize();

        using var workspace = manager.AllocateWorkspace();

        Assert.True(Directory.Exists(workspace.Path));
        Assert.True(File.Exists(Path.Combine(workspace.Path, ScratchWorkspaceManager.OwnershipMarkerFileName)));
        // Workspace path should be below scratch root
        Assert.StartsWith(scratchRoot, workspace.Path);
        // Workspace ID should be opaque (hex string, not derived from entry names)
        Assert.True(workspace.Id.Length >= 16);
    }

    [Fact]
    public void AllocateWorkspace_GeneratesUniqueOpaqueNames()
    {
        var scratchRoot = Path.Combine(_tempDir, "scratch");
        var manager = new ScratchWorkspaceManager(scratchRoot);
        manager.Initialize();

        var ws1 = manager.AllocateWorkspace();
        var ws2 = manager.AllocateWorkspace();

        Assert.NotEqual(ws1.Path, ws2.Path);
        Assert.NotEqual(ws1.Id, ws2.Id);
    }

    [Fact]
    public void IsPathContainedIn_RejectsPathTraversal()
    {
        var scratchRoot = Path.Combine(_tempDir, "scratch");
        var manager = new ScratchWorkspaceManager(scratchRoot);
        manager.Initialize();

        using var workspace = manager.AllocateWorkspace();

        // Valid path inside workspace
        var validPath = Path.Combine(workspace.Path, "output.png");
        Assert.True(manager.IsPathContainedIn(workspace.Path, validPath));

        // Path traversal attempt — escapes workspace
        var traversalPath = Path.GetFullPath(Path.Combine(workspace.Path, "..", "..", "escape.png"));
        Assert.False(manager.IsPathContainedIn(workspace.Path, traversalPath));
    }

    [Fact]
    public void CleanupWorkspace_RemovesWorkspaceAfterDisposal()
    {
        var scratchRoot = Path.Combine(_tempDir, "scratch");
        var manager = new ScratchWorkspaceManager(scratchRoot);
        manager.Initialize();

        var workspace = manager.AllocateWorkspace();
        var wsPath = workspace.Path;

        // Write a file to simulate worker output
        File.WriteAllText(Path.Combine(wsPath, "output.png"), "fake");

        workspace.Dispose();

        Assert.False(Directory.Exists(wsPath));
    }

    [Fact]
    public void CleanupWorkspace_OnlyRemovesOwnedWorkspaces()
    {
        var scratchRoot = Path.Combine(_tempDir, "scratch");
        var manager = new ScratchWorkspaceManager(scratchRoot);
        manager.Initialize();

        // Create an unfamiliar directory (no ownership marker)
        var unfamiliarDir = Path.Combine(scratchRoot, "unfamiliar");
        Directory.CreateDirectory(unfamiliarDir);
        File.WriteAllText(Path.Combine(unfamiliarDir, "data.txt"), "not ours");

        manager.CleanupWorkspace(unfamiliarDir);

        // Unfamiliar directory should NOT be deleted
        Assert.True(Directory.Exists(unfamiliarDir));
    }

    [Fact]
    public void RecoverInactiveWorkspaces_RemovesOnlyOwnedInactiveWorkspaces()
    {
        var scratchRoot = Path.Combine(_tempDir, "scratch");
        var manager = new ScratchWorkspaceManager(scratchRoot);
        manager.Initialize();

        // Create an owned workspace with old timestamp
        var oldWorkspace = manager.AllocateWorkspace();
        File.WriteAllText(Path.Combine(oldWorkspace.Path, "data.bin"), "old");
        // Set last write time to the past
        Directory.SetLastWriteTimeUtc(oldWorkspace.Path, DateTime.UtcNow.AddDays(-2));
        oldWorkspace.Dispose(); // dispose doesn't matter — we're testing recovery

        // Recreate the directory since Dispose removed it
        Directory.CreateDirectory(oldWorkspace.Path);
        File.WriteAllText(Path.Combine(oldWorkspace.Path, ScratchWorkspaceManager.OwnershipMarkerFileName), "owned");
        File.WriteAllText(Path.Combine(oldWorkspace.Path, "data.bin"), "old");
        Directory.SetLastWriteTimeUtc(oldWorkspace.Path, DateTime.UtcNow.AddDays(-2));

        // Create an unfamiliar directory
        var unfamiliarDir = Path.Combine(scratchRoot, "unfamiliar");
        Directory.CreateDirectory(unfamiliarDir);

        // Create a recent owned workspace
        var recentWorkspace = manager.AllocateWorkspace();

        var cleaned = manager.RecoverInactiveWorkspaces(TimeSpan.FromDays(1));

        Assert.Equal(1, cleaned);
        Assert.False(Directory.Exists(oldWorkspace.Path));
        Assert.True(Directory.Exists(unfamiliarDir));
        Assert.True(Directory.Exists(recentWorkspace.Path));
    }

    [Fact]
    public void IsOwnedWorkspace_ReturnsTrueForWorkspaceWithMarker()
    {
        var scratchRoot = Path.Combine(_tempDir, "scratch");
        var manager = new ScratchWorkspaceManager(scratchRoot);
        manager.Initialize();

        using var workspace = manager.AllocateWorkspace();
        Assert.True(manager.IsOwnedWorkspace(workspace.Path));

        var unfamiliarDir = Path.Combine(scratchRoot, "unfamiliar");
        Directory.CreateDirectory(unfamiliarDir);
        Assert.False(manager.IsOwnedWorkspace(unfamiliarDir));
    }

    [Fact]
    public void HasAvailableBudget_ReturnsTrueWhenUnderBudget()
    {
        var scratchRoot = Path.Combine(_tempDir, "scratch");
        var manager = new ScratchWorkspaceManager(scratchRoot, scratchBudgetBytes: 1024 * 1024);
        manager.Initialize();

        using var workspace = manager.AllocateWorkspace();
        File.WriteAllText(Path.Combine(workspace.Path, "small.bin"), new string('x', 100));

        Assert.True(manager.HasAvailableBudget(1024));
        Assert.False(manager.HasAvailableBudget(2 * 1024 * 1024));
    }

    [Fact]
    public void GetCurrentUsageBytes_ReportsOwnedWorkspaceSizes()
    {
        var scratchRoot = Path.Combine(_tempDir, "scratch");
        var manager = new ScratchWorkspaceManager(scratchRoot);
        manager.Initialize();

        using var workspace = manager.AllocateWorkspace();
        File.WriteAllText(Path.Combine(workspace.Path, "data.bin"), new string('x', 500));

        var usage = manager.GetCurrentUsageBytes();
        Assert.True(usage >= 500);
    }
}
