namespace com.lifepixer.mangapixer.Server.Storage;

using System.IO;

/// <summary>
/// Read-only filesystem abstraction for library source media.
/// All operations are explicitly read-only — no write, delete, or rename methods exist.
/// This is a critical safety invariant: the server never modifies source media.
/// </summary>
public interface IReadOnlyLibraryFileSystem
{
    /// <summary>
    /// Returns true if the root path exists and is accessible.
    /// </summary>
    bool RootExists();

    /// <summary>
    /// Returns the root directory info (without following symlinks).
    /// </summary>
    DirectoryEntry? GetRoot();

    /// <summary>
    /// Enumerates child entries of a directory. Returns empty if not accessible.
    /// </summary>
    IReadOnlyList<FileSystemEntry> EnumerateEntries(string relativePath);

    /// <summary>
    /// Returns entry info for a specific relative path, or null if not found.
    /// </summary>
    FileSystemEntry? GetEntry(string relativePath);

    /// <summary>
    /// Opens a file for read-only access. Throws if the file is not accessible.
    /// The returned stream is read-only and non-writable.
    /// </summary>
    Stream OpenRead(string relativePath);

    /// <summary>
    /// Returns a source stamp (last write time + byte length) for change detection.
    /// </summary>
    SourceStamp GetSourceStamp(string relativePath);

    /// <summary>
    /// Returns the observed root identity (volume serial / device ID / inode) for mount-change detection.
    /// </summary>
    string? GetRootIdentity();
}

/// <summary>
/// Filesystem entry kind.
/// </summary>
public enum EntryKind
{
    Directory = 0,
    File = 1,
}

/// <summary>
/// A filesystem entry (file or directory) with read-only metadata.
/// </summary>
public sealed class FileSystemEntry
{
    public required string RelativePath { get; init; }
    public required string Name { get; init; }
    public required EntryKind Kind { get; init; }
    public long ByteLength { get; init; }
    public DateTimeOffset LastWriteTimeUtc { get; init; }
    public bool IsHidden { get; init; }
    public bool IsSymlink { get; init; }
}

/// <summary>
/// Directory entry with additional metadata.
/// </summary>
public sealed class DirectoryEntry
{
    public required string RelativePath { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset LastWriteTimeUtc { get; init; }
    public bool IsHidden { get; init; }
    public bool IsSymlink { get; init; }
}

/// <summary>
/// Source stamp for change detection. Combines last write time and byte length.
/// Two stamps are equal if both fields match.
/// </summary>
public readonly record struct SourceStamp(long LastWriteTicks, long ByteLength)
{
    public static SourceStamp None => new(0, 0);

    public bool Equals(SourceStamp other) =>
        LastWriteTicks == other.LastWriteTicks && ByteLength == other.ByteLength;

    public override int GetHashCode() => HashCode.Combine(LastWriteTicks, ByteLength);
}
