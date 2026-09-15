namespace com.lifepixer.mangaplex.MediaWorker.Archives;

using com.lifepixer.mangaplex.Core.Media;

/// <summary>
/// Detects archive format from file signature (magic bytes), not extension.
/// Supports ZIP, RAR4, RAR5, and 7z as defined in release one.
/// </summary>
public static class ArchiveFormatDetector
{
    // ZIP signature: PK\x03\x04 (also PK\x05\x06 for empty, PK\x07\x08 for spanned)
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipEmptySignature = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] ZipSpannedSignature = [0x50, 0x4B, 0x07, 0x08];

    // RAR4 signature: Rar!\x1A\x07\x00
    private static readonly byte[] Rar4Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    // RAR5 signature: Rar!\x1A\x07\x01\x00
    private static readonly byte[] Rar5Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    // 7z signature: 7z\xBC\xAF\x27\x1C
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    /// <summary>
    /// Detects the archive format from the first bytes of a file.
    /// Returns Unknown if the signature does not match any supported format.
    /// </summary>
    public static ArchiveFormat Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 && header[..4].SequenceEqual(ZipSignature))
            return ArchiveFormat.Zip;
        if (header.Length >= 4 && header[..4].SequenceEqual(ZipEmptySignature))
            return ArchiveFormat.Zip;
        if (header.Length >= 4 && header[..4].SequenceEqual(ZipSpannedSignature))
            return ArchiveFormat.Zip; // Multipart ZIP — detected but rejected later
        if (header.Length >= 7 && header[..7].SequenceEqual(Rar4Signature))
            return ArchiveFormat.Rar4;
        if (header.Length >= 8 && header[..8].SequenceEqual(Rar5Signature))
            return ArchiveFormat.Rar5;
        if (header.Length >= 6 && header[..6].SequenceEqual(SevenZipSignature))
            return ArchiveFormat.SevenZip;
        return ArchiveFormat.Unknown;
    }

    /// <summary>
    /// Detects the archive format by reading the first 16 bytes of a file.
    /// </summary>
    public static async Task<ArchiveFormat> DetectFromFileAsync(string path, CancellationToken ct = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 16, useAsync: true);
        var buffer = new byte[16];
        int read = await stream.ReadAsync(buffer, ct);
        return Detect(buffer.AsSpan(0, read));
    }

    /// <summary>
    /// Returns the expected file extension for an archive format.
    /// </summary>
    public static string GetExtension(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => ".cbz",
        ArchiveFormat.Rar4 or ArchiveFormat.Rar5 => ".cbr",
        ArchiveFormat.SevenZip => ".cb7",
        _ => ".unknown",
    };
}
