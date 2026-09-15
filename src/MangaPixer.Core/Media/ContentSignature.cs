namespace com.lifepixer.mangaplex.Core.Media;

using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

/// <summary>
/// Cheap, stored content signature used to recognise an archive that merely
/// moved or was renamed between two scans (1.5.0).
///
/// Format: <c>v1:&lt;byteLength&gt;:&lt;sha256-hex&gt;</c>, where the hash covers
/// the byte length, the first <see cref="SampleBytes"/> and the last
/// <see cref="SampleBytes"/> of the file. Two files with the same length whose
/// heads or tails differ therefore produce different signatures; for ZIP/RAR/7z
/// the tail carries the central directory / end-of-archive record, so any
/// change to the entry list or offsets shows up there even when the length is
/// preserved. This is deliberately not a full-content hash: it reads at most
/// 128 KiB per file so it can be computed on a network share without
/// re-reading whole archives.
///
/// A match is only ever used to <em>avoid</em> a tombstone+create pair; the
/// callers fall back to the safe default (treat as removed + added) whenever a
/// signature is missing or ambiguous.
/// </summary>
public static class ContentSignature
{
    /// <summary>Bytes hashed from each end of the file.</summary>
    public const int SampleBytes = 64 * 1024;

    /// <summary>Format prefix; bump when the hashing scheme changes.</summary>
    public const string FormatVersion = "v1";

    /// <summary>
    /// Upper bound on the encoded length ("v1:" + 20-digit length + ":" + 64 hex).
    /// Used for the persisted column width.
    /// </summary>
    public const int MaxLength = 96;

    /// <summary>
    /// Computes the signature of a seekable, readable stream. The stream position
    /// is not preserved.
    /// </summary>
    public static string Compute(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
            throw new ArgumentException("Stream must be seekable and readable.", nameof(stream));

        var length = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> lengthBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
        hash.AppendData(lengthBytes);

        var buffer = new byte[SampleBytes];

        // Head
        stream.Position = 0;
        var headBytes = (int)Math.Min(SampleBytes, length);
        ReadExactly(stream, buffer.AsSpan(0, headBytes));
        hash.AppendData(buffer, 0, headBytes);

        // Tail (only the part not already covered by the head)
        if (length > SampleBytes)
        {
            var tailStart = Math.Max(SampleBytes, length - SampleBytes);
            var tailBytes = (int)(length - tailStart);
            stream.Position = tailStart;
            ReadExactly(stream, buffer.AsSpan(0, tailBytes));
            hash.AppendData(buffer, 0, tailBytes);
        }

        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        return $"{FormatVersion}:{length}:{digest}";
    }

    /// <summary>
    /// Computes the signature of a file opened read-only with read sharing, or
    /// returns null when the file cannot be read. Never throws.
    /// </summary>
    public static string? TryComputeFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
            return Compute(stream);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>
    /// Returns the byte length embedded in a signature, or null if the value is
    /// not a well-formed signature. Lets callers cross-check a stored signature
    /// against an observed file length without re-hashing.
    /// </summary>
    public static long? TryGetByteLength(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        var parts = signature.Split(':');
        if (parts.Length != 3 || parts[0] != FormatVersion) return null;
        return long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var len) ? len : null;
    }

    private static void ReadExactly(Stream stream, Span<byte> target)
    {
        while (!target.IsEmpty)
        {
            var read = stream.Read(target);
            if (read <= 0) throw new EndOfStreamException("File shrank while computing its content signature.");
            target = target[read..];
        }
    }
}
