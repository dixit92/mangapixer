namespace com.lifepixer.mangapixer.Server.Storage;

using com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// Read-only directory browser for the admin library-registration path picker.
///
/// Listing is strictly confined to a single configured media browse root
/// (<see cref="MediaBrowseOptions.Root"/>, default <c>/media</c> — the
/// conventional read-only media mount). A requested path outside that root is
/// clamped back to the root rather than served, so path traversal
/// (<c>../</c>, absolute escapes) cannot reach the wider filesystem. Only
/// directories are listed — never files or their contents. The endpoint that
/// exposes this is admin-only.
/// </summary>
public sealed class FilesystemBrowseService
{
    private readonly MediaBrowseOptions _options;

    public FilesystemBrowseService(MediaBrowseOptions options)
    {
        _options = options;
    }

    public DirectoryListingDto Browse(string? requestedPath)
    {
        var root = Canonicalize(_options.Root);
        if (root is null || !Directory.Exists(root))
        {
            // No browsable root configured/accessible — the admin types a path.
            return new DirectoryListingDto { Available = false };
        }

        // Resolve the requested path and confine it to the root. Anything that
        // does not sit at or under the root is clamped to the root.
        var target = Canonicalize(requestedPath);
        if (target is null || !(PathEquals(target, root) || IsNested(target, root)) || !Directory.Exists(target))
        {
            target = root;
        }

        string? parent = null;
        if (!PathEquals(target, root))
        {
            var p = Canonicalize(Directory.GetParent(target)?.FullName);
            if (p is not null && (PathEquals(p, root) || IsNested(p, root)))
                parent = p;
            else
                parent = root;
        }

        var entries = new List<DirectoryEntryDto>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(target))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name)) continue;

                // Never offer hidden (dot-prefixed) directories as library roots —
                // e.g. .yacreaderlibrary, .git (matches the scan policy).
                if (name.StartsWith('.')) continue;

                entries.Add(new DirectoryEntryDto
                {
                    Name = name,
                    Path = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    HasChildren = HasSubdirectories(dir),
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A directory we cannot read yields an empty listing rather than an error.
        }

        entries.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return new DirectoryListingDto
        {
            Available = true,
            Root = root,
            Current = target,
            Parent = parent,
            Entries = entries,
        };
    }

    private static bool HasSubdirectories(string dir)
    {
        try
        {
            // Only count visible subdirectories, so a folder whose only children
            // are hidden (e.g. .yacreaderlibrary) is not shown as expandable.
            return Directory.EnumerateDirectories(dir)
                .Any(d => !Path.GetFileName(d).StartsWith('.'));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string? Canonicalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsNested(string child, string parent)
    {
        var parentWithSep = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(parentWithSep, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Configuration for the admin directory browser. <see cref="Root"/> is the only
/// path the browser will ever list under; default is the container media mount.
/// </summary>
public sealed class MediaBrowseOptions
{
    public string? Root { get; set; }
}
