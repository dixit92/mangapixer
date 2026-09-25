namespace com.lifepixer.mangapixer.MediaWorker.Archives;

using com.lifepixer.mangapixer.Core.Media;
using SharpCompress.Archives;
using System.Security.Cryptography;

/// <summary>
/// Archive reader adapter using SharpCompress.
/// Enumerates entries without extracting, detects format, and identifies issues.
/// Solid-archive support and enforced resource limits (entry count, uncompressed
/// bytes budget) are not yet implemented here; the worker's IPC contract with the
/// server is implemented in WorkerLoop.
/// </summary>
public sealed class ArchiveReader : IDisposable
{
    private IArchive? _archive;
    private readonly string _path;
    private readonly ArchiveFormat _format;

    private ArchiveReader(string path, ArchiveFormat format, IArchive archive)
    {
        _path = path;
        _format = format;
        _archive = archive;
    }

    /// <summary>
    /// Opens an archive for reading. Detects format from file signature.
    /// Uses ArchiveFactory.OpenArchive for format-agnostic opening.
    /// </summary>
    public static async Task<ArchiveReader> OpenAsync(string path, CancellationToken ct = default)
    {
        var format = await ArchiveFormatDetector.DetectFromFileAsync(path, ct);
        if (format == ArchiveFormat.Unknown)
            throw new InvalidDataException($"Unknown archive format: {Path.GetFileName(path)}");

        var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(path);
        return new ArchiveReader(path, format, archive);
    }

    /// <summary>
    /// The detected archive format.
    /// </summary>
    public ArchiveFormat Format => _format;

    /// <summary>
    /// Whether the archive is solid (entries share a compression stream, so reaching
    /// entry N requires decompressing 0..N). Used to defer per-page random-access
    /// extraction for solid archives — reading pages out of a solid archive is not
    /// yet supported, so callers check this and fail gracefully instead.
    /// </summary>
    public bool IsSolid => _archive?.IsSolid ?? false;

    /// <summary>
    /// Enumerates all entries in the archive without extracting.
    /// Returns entry metadata only — no image bytes are read.
    /// </summary>
    public ArchiveEnumerationResult EnumerateEntries()
    {
        ObjectDisposedException.ThrowIf(_archive is null, this);

        var entries = new List<ArchiveEntryInfo>();
        bool isSolid = false;
        bool isEncrypted = false;
        bool isMultipart = false;
        string? error = null;
        int ordinal = 0;

        try
        {
            isSolid = _archive.IsSolid;

            // Check for encrypted entries
            foreach (var entry in _archive.Entries)
            {
                if (entry.IsEncrypted)
                    isEncrypted = true;

                // Check for multipart (split after)
                if (entry.IsSplitAfter)
                    isMultipart = true;

                var entryPath = entry.Key ?? string.Empty;
                var isDir = entry.IsDirectory;

                entries.Add(new ArchiveEntryInfo
                {
                    Ordinal = ordinal++,
                    EntryPath = entryPath,
                    IsDirectory = isDir,
                    UncompressedSize = entry.Size,
                    IsEncrypted = entry.IsEncrypted,
                });
            }
        }
        catch (CryptographicException)
        {
            isEncrypted = true;
            error = "Archive is encrypted";
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }

        return new ArchiveEnumerationResult
        {
            Format = _format,
            Entries = entries.AsReadOnly(),
            IsSolid = isSolid,
            IsEncrypted = isEncrypted,
            IsMultipart = isMultipart,
            Error = error,
        };
    }

    /// <summary>
    /// Extracts a single entry to a stream. For ZIP/non-solid archives, this is
    /// a random-access operation. For solid archives, this would require sequential
    /// decompression from the start of the stream, which is not yet implemented —
    /// callers should check <see cref="IsSolid"/> first and avoid calling this.
    /// </summary>
    public async Task<Stream> ExtractEntryAsync(string entryKey, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_archive is null, this);

        var entry = _archive.Entries.FirstOrDefault(e => e.Key == entryKey)
            ?? throw new FileNotFoundException($"Entry not found: {entryKey}");

        if (entry.IsDirectory)
            throw new InvalidOperationException("Cannot extract a directory entry");

        var ms = new MemoryStream();
        using (var entryStream = entry.OpenEntryStream())
        {
            await entryStream.CopyToAsync(ms, ct);
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Reads one entry fully into memory, but never more than
    /// <paramref name="maxBytes"/> bytes: returns null as soon as the decompressed
    /// stream exceeds the cap, whatever the entry header declared (a lying size
    /// field cannot turn a small metadata read into a decompression bomb). For
    /// random-access archives only - callers check <see cref="IsSolid"/> first.
    /// </summary>
    public async Task<byte[]?> ReadEntryBoundedAsync(string entryKey, long maxBytes, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_archive is null, this);

        var entry = _archive.Entries.FirstOrDefault(e => e.Key == entryKey)
            ?? throw new FileNotFoundException("Entry not found");
        if (entry.IsDirectory)
            throw new InvalidOperationException("Cannot read a directory entry");

        using var entryStream = entry.OpenEntryStream();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await entryStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
                return null;
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        _archive?.Dispose();
        _archive = null;
    }
}
