namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using System.Globalization;
using com.lifepixer.mangapixer.Server.Logging;

/// <summary>
/// Durable store for provider series posters (1.24.0, lane B2) under
/// <c>DataRoot/metadata-images/{recordId}-{imageVersion}.{ext}</c>. Bytes are
/// stored VERBATIM after a magic-byte check (JPEG / PNG / WebP / GIF) - the server
/// never decodes an image (decoding untrusted images is a worker job). Publish is
/// atomic (temp file + rename, the <c>ThumbnailStore</c> pattern). Registered as
/// an <see cref="IMetadataRecordRemovedHandler"/>, so a record's images go with it.
/// File names carry numeric ids only.
/// </summary>
public sealed class MetadataImageStore : IMetadataRecordRemovedHandler
{
    private static readonly string[] s_extensions = ["jpg", "png", "webp", "gif"];

    private readonly string _root;
    private readonly ILogger<MetadataImageStore>? _logger;

    public MetadataImageStore(string root, ILogger<MetadataImageStore>? logger = null)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _logger = logger;
    }

    public string Root => _root;

    /// <summary>The image kind by magic bytes (<c>jpg</c>, <c>png</c>, <c>webp</c>, <c>gif</c>), or null for anything else.</summary>
    public static string? DetectExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "jpg";
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return "webp";
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return "gif";
        return null;
    }

    public static string ContentTypeFor(string extension) => extension switch
    {
        "jpg" => "image/jpeg",
        "png" => "image/png",
        "webp" => "image/webp",
        "gif" => "image/gif",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Stores <paramref name="bytes"/> as the record's image version
    /// <paramref name="imageVersion"/> and removes its older versions. Throws
    /// <see cref="MetadataResponseInvalidException"/> when the bytes are not a
    /// supported image.
    /// </summary>
    public async Task PublishAsync(long recordId, int imageVersion, byte[] bytes, CancellationToken ct = default)
    {
        var extension = DetectExtension(bytes) ?? throw new MetadataResponseInvalidException("not_an_image");
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, FileName(recordId, imageVersion, extension));
        var temp = Path.Combine(_root, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        foreach (var old in Directory.EnumerateFiles(_root, $"{recordId.ToString(CultureInfo.InvariantCulture)}-*"))
        {
            if (!string.Equals(old, destination, StringComparison.Ordinal))
                TryDelete(old);
        }
        _logger?.LogDebug(LogEvents.Metadata.ImageStored, "Series image stored for record {RecordId} (version {Version})", recordId, imageVersion);
    }

    /// <summary>Opens the stored image of a record version, or null when it does not exist.</summary>
    public (Stream Stream, string ContentType)? Open(long recordId, int imageVersion)
    {
        foreach (var extension in s_extensions)
        {
            var path = Path.Combine(_root, FileName(recordId, imageVersion, extension));
            try
            {
                if (File.Exists(path))
                    return (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), ContentTypeFor(extension));
            }
            catch (IOException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Deletes every stored image of the given records.</summary>
    public void DeleteRecords(IEnumerable<long> recordIds)
    {
        if (!Directory.Exists(_root))
            return;
        foreach (var id in recordIds)
        {
            foreach (var file in Directory.EnumerateFiles(_root, $"{id.ToString(CultureInfo.InvariantCulture)}-*"))
                TryDelete(file);
        }
    }

    public Task OnRecordsRemovedAsync(IReadOnlyList<long> recordIds, CancellationToken ct)
    {
        DeleteRecords(recordIds);
        return Task.CompletedTask;
    }

    private static string FileName(long recordId, int imageVersion, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"{recordId}-{imageVersion}.{extension}");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
