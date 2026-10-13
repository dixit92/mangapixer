namespace com.lifepixer.mangapixer.MediaWorker.Archives;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Images;

/// <summary>
/// The <c>page_hash</c> op (1.43.0 volume contents scan; PROTOTYPE in phase 1): every image page of ONE archive, hashed in one
/// pass - SHA-256 of the entry bytes and / or the 64-bit perceptual hash - in reading order (the same entries and order as
/// <c>analyze</c>). Opens the archive read-only, checks the source stamp before and after, writes nothing. Bounded: page count
/// and total size before reading, page size and dimensions per page, the deadline throughout.
/// </summary>
public static class PageHashOperation
{
    /// <summary>The answer: a result, or an archive-level error code.</summary>
    public sealed record Outcome(PageHashResult? Result, string? Error);

    public static async Task<Outcome> RunAsync(PageHashRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sw = Stopwatch.StartNew();
        var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;

        var maxPages = Clamp(request.MaxPages, PageHashLimits.MaxPages);
        var maxPageBytes = Clamp(request.MaxPageBytes, PageHashLimits.MaxPageBytes);
        var maxTotalBytes = Clamp(request.MaxTotalBytes, PageHashLimits.MaxTotalBytes);
        var maxDimension = Clamp(request.MaxDimension, PageHashLimits.MaxDimension);
        var probe = ParseProbe(request.ProbeTransform);

        if (string.IsNullOrEmpty(request.ArchivePath) || !File.Exists(request.ArchivePath))
            return new Outcome(null, PageHashErrors.Missing);
        if (StampOf(request.ArchivePath) != (request.ExpectedLastWriteTicks, request.ExpectedByteLength))
            return new Outcome(null, PageHashErrors.SourceChanged);

        using var reader = await ArchiveReader.OpenAsync(request.ArchivePath, ct).ConfigureAwait(false);
        var enumeration = reader.EnumerateEntries();
        if (enumeration.IsEncrypted)
            return new Outcome(null, PageHashErrors.Encrypted);
        if (enumeration.Error is not null)
            return new Outcome(null, PageHashErrors.ReadFailed);

        // The pages, as analysis orders them: image entries, ignored names dropped, natural order of the entry path.
        var pages = enumeration.Entries
            .Where(e => e.IsImageCandidate && !ImageExtensions.ShouldIgnore(e.EntryPath))
            .OrderBy(e => e.EntryPath, NaturalOrderComparer.Instance)
            .ToList();
        if (pages.Count > maxPages)
            return new Outcome(null, PageHashErrors.TooManyPages);
        if (pages.Sum(p => Math.Max(0, p.UncompressedSize)) > maxTotalBytes)
            return new Outcome(null, PageHashErrors.TooLarge);
        var ordinalOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < pages.Count; i++)
            ordinalOf.TryAdd(pages[i].EntryPath, i);

        var hashed = new PageHashEntry?[pages.Count];
        long readTicks = 0, exactTicks = 0, decodeTicks = 0, total = 0;
        var phase = new Stopwatch();
        var deadlineHit = false;
        try
        {
            await reader.ForEachEntryAsync(async (key, size, stream) =>
            {
                if (!ordinalOf.TryGetValue(key, out var ordinal) || hashed[ordinal] is not null)
                    return;
                if (DateTimeOffset.UtcNow > request.Deadline)
                {
                    deadlineHit = true;
                    throw new OperationCanceledException();
                }
                if (size > maxPageBytes)
                {
                    hashed[ordinal] = new PageHashEntry { Ordinal = ordinal, ByteSize = size, Error = PageHashErrors.TooLarge };
                    return;
                }
                phase.Restart();
                var bytes = await ReadBoundedAsync(stream, maxPageBytes, ct).ConfigureAwait(false);
                readTicks += phase.ElapsedTicks;
                if (bytes is null)
                {
                    hashed[ordinal] = new PageHashEntry { Ordinal = ordinal, ByteSize = size, Error = PageHashErrors.TooLarge };
                    return;
                }
                total += bytes.Length;

                phase.Restart();
                var sha = request.Exact ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : null;
                exactTicks += phase.ElapsedTicks;

                phase.Restart();
                HashedImage? image = null;
                ulong? other = null, probed = null;
                if (request.Perceptual)
                {
                    image = ImageHasher.Hash(bytes, maxDimension, request.FastDecode);
                    if (request.ProbeBothDecodes && image.Error is null)
                    {
                        var second = ImageHasher.Hash(bytes, maxDimension, !request.FastDecode);
                        other = second.Error is null ? second.Hash : null;
                    }
                    if (probe is { } p && image.Error is null && ImageHasher.Reencoded(bytes, p.Scale, p.Quality, p.Crop, p.Blank) is { } copy)
                    {
                        var copyHash = ImageHasher.Hash(copy, maxDimension, request.FastDecode);
                        probed = copyHash.Error is null ? copyHash.Hash : null;
                    }
                }
                decodeTicks += phase.ElapsedTicks;

                hashed[ordinal] = new PageHashEntry
                {
                    Ordinal = ordinal,
                    ByteSize = bytes.Length,
                    Width = image?.Width ?? 0,
                    Height = image?.Height ?? 0,
                    Sha256 = sha,
                    Hash = image is { Error: null } ? image.Hash : null,
                    OtherDecodeHash = other,
                    ProbeHash = probed,
                    Error = image?.Error is { } e ? (e == ImageHashErrors.TooLarge ? PageHashErrors.TooLarge : PageHashErrors.DecodeFailed) : null,
                };
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadlineHit)
        {
            return new Outcome(null, PageHashErrors.Deadline);
        }
        catch (CryptographicException)
        {
            return new Outcome(null, PageHashErrors.Encrypted);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or NotSupportedException
            or SharpCompress.Common.ArchiveException)
        {
            return new Outcome(null, PageHashErrors.ReadFailed);
        }

        if (StampOf(request.ArchivePath) != (request.ExpectedLastWriteTicks, request.ExpectedByteLength))
            return new Outcome(null, PageHashErrors.SourceChanged);

        var list = hashed.Select((h, i) => h ?? new PageHashEntry { Ordinal = i, Error = PageHashErrors.ReadFailed }).ToList();
        process.Refresh();
        var (ticks, bytesLength) = StampOf(request.ArchivePath);
        return new Outcome(new PageHashResult
        {
            JobId = request.JobId,
            Pages = list,
            ObservedLastWriteTicks = ticks,
            ObservedByteLength = bytesLength,
            ElapsedMs = (int)sw.ElapsedMilliseconds,
            CpuMs = (int)(process.TotalProcessorTime - cpuStart).TotalMilliseconds,
            ReadMs = Ms(readTicks),
            ExactMs = Ms(exactTicks),
            DecodeMs = Ms(decodeTicks),
            PeakWorkingSetBytes = process.PeakWorkingSet64,
            TotalBytes = total,
        }, null);
    }

    private static int Ms(long ticks) => (int)(ticks * 1000 / Stopwatch.Frequency);

    private static int Clamp(int requested, int max) => requested > 0 ? Math.Min(requested, max) : max;

    private static long Clamp(long requested, long max) => requested > 0 ? Math.Min(requested, max) : max;

    /// <summary>Reads the stream fully, or null as soon as it passes <paramref name="maxBytes"/> (a lying size field cannot inflate).</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long count = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            count += read;
            if (count > maxBytes)
                return null;
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static (double Scale, int Quality, double Crop, double Blank)? ParseProbe(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var parts = text.Split(':');
        var blank = 0.0;
        if (parts.Length == 4 && (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out blank) || blank is < 0 or > 0.5))
            return null;
        if (parts.Length is not (3 or 4)
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var crop)
            || scale is <= 0.05 or > 2 || quality is < 1 or > 100 || crop is < 0 or > 0.2)
            return null;
        return (scale, quality, crop, blank);
    }

    internal static (long LastWriteTicks, long ByteLength) StampOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0, 0);
    }
}
