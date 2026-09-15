namespace com.lifepixer.mangapixer.TestSupport;

/// <summary>
/// Shared test support utilities for MangaPixer tests.
/// </summary>
public static class TestSupport
{
    /// <summary>
    /// Creates a unique temporary directory path for test isolation.
    /// Actual directory creation is deferred to the caller.
    /// </summary>
    public static string GetTempTestRoot(string prefix = "mangapixer-test")
    {
        return Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Creates a unique temporary directory and returns its path.
    /// </summary>
    public static string CreateTempTestRoot(string prefix = "mangapixer-test")
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
