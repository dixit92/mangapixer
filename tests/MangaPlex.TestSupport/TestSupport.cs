namespace com.lifepixer.mangaplex.TestSupport;

/// <summary>
/// Shared test support utilities for MangaPlex tests.
/// </summary>
public static class TestSupport
{
    /// <summary>
    /// Creates a unique temporary directory path for test isolation.
    /// Actual directory creation is deferred to the caller.
    /// </summary>
    public static string GetTempTestRoot(string prefix = "mangaplex-test")
    {
        return Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Creates a unique temporary directory and returns its path.
    /// </summary>
    public static string CreateTempTestRoot(string prefix = "mangaplex-test")
    {
        var path = GetTempTestRoot(prefix);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Safely deletes a directory tree if it exists.
    /// </summary>
    public static void CleanupDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }
}
