namespace com.lifepixer.mangapixer.MediaWorker.Archives;

using System.Diagnostics;
using System.Security.Cryptography;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Images;

/// <summary>
/// The <c>page_hash</c> op (worker protocol v6, 1.44.0 volume contents scan): the 64-bit perceptual hash of a range of the image pages
/// of ONE archive. Pages are the entries <c>analyze</c> counts, in its order, so an ordinal here is a stored page ordinal. The archive is
/// opened read-only (SharpCompress over a read-access file stream), the source stamp is checked before and after, nothing is written.
/// A random-access archive reads only the requested entries; a solid archive is decompressed front to back once (every page from the
/// requested ordinal to the end) and only up to <see cref="PageHashLimits.MaxSolidBytes"/>. JPEG pages decode at a reduced scale
/// (<see cref="ImageHasher.ReducedDecodeSide"/>): 0-2 bits from the full decode at p95, about 4x cheaper (1.43.0 measurement).
/// </summary>
public static class PageHashOperation
{
    public sealed record Outcome(PageHashResult? Result, string? Error);

    public static async Task<Outcome> RunAsync(PageHashRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sw = Stopwatch.StartNew();
        var maxPages = Clamp(request.MaxPages, PageHashLimits.MaxPages);
        var maxPageBytes = Clamp(request.MaxPageBytes, PageHashLimits.MaxPageBytes);
        var maxTotalBytes = Clamp(request.MaxTotalBytes, PageHashLimits.MaxTotalBytes);
        var maxDimension = Clamp(request.MaxDimension, PageHashLimits.MaxDimension);
        if (request.FromOrdinal < 0 || request.Count < 0)
            return new Outcome(null, PageHashErrors.InvalidRequest);

        if (string.IsNullOrEmpty(request.ArchivePath) || !File.Exists(request.ArchivePath))
            return new Outcome(null, PageHashErrors.Missing);
        if (StampOf(request.ArchivePath) != (request.ExpectedLastWriteTicks, request.ExpectedByteLength))
            return new Outcome(null, PageHashErrors.SourceChanged);

        List<PageHashEntry> hashed;
        bool solid;
        int pageCount;
        var deadlineHit = false;
        try
        {
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
            pageCount = pages.Count;
            if (pages.Count > maxPages)
                return new Outcome(null, PageHashErrors.TooManyPages);
            if (pages.Sum(p => Math.Max(0, p.UncompressedSize)) > maxTotalBytes)
                return new Outcome(null, PageHashErrors.TooLarge);
            solid = reader.IsSolid;
            if (solid && request.ExpectedByteLength > PageHashLimits.MaxSolidBytes)
                return new Outcome(null, PageHashErrors.TooLarge);

            var from = Math.Min(request.FromOrdinal, pages.Count);
            var to = solid || request.Count == 0 ? pages.Count : Math.Min(pages.Count, from + request.Count);
            var slots = new PageHashEntry?[to - from];

            if (solid)
            {
                var ordinalOf = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = from; i < to; i++)
                    ordinalOf.TryAdd(pages[i].EntryPath, i);
                await reader.ForEachEntryAsync(async (key, size, stream) =>
                {
                    if (!ordinalOf.TryGetValue(key, out var ordinal) || slots[ordinal - from] is not null)
                        return;
                    CheckDeadline(request, ref deadlineHit);
                    var bytes = size > maxPageBytes ? null : await ReadBoundedAsync(stream, maxPageBytes, ct).ConfigureAwait(false);
                    slots[ordinal - from] = HashPage(ordinal, bytes, maxDimension);
                }, ct).ConfigureAwait(false);
            }
            else
            {
                for (var ordinal = from; ordinal < to; ordinal++)
                {
                    ct.ThrowIfCancellationRequested();
                    CheckDeadline(request, ref deadlineHit);
                    var page = pages[ordinal];
                    var bytes = page.UncompressedSize > maxPageBytes
                        ? null
                        : await reader.ReadEntryBoundedAsync(page.EntryPath, maxPageBytes, ct).ConfigureAwait(false);
                    slots[ordinal - from] = HashPage(ordinal, bytes, maxDimension);
                }
            }
            hashed = slots.Select((h, i) => h ?? new PageHashEntry { Ordinal = from + i, Error = PageHashErrors.ReadFailed }).ToList();
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
            or UnauthorizedAccessException or SharpCompress.Common.ArchiveException)
        {
            return new Outcome(null, PageHashErrors.ReadFailed);
        }

        // Changed while it was read: the hashes may mix two versions of the file.
        var (ticks, length) = StampOf(request.ArchivePath);
        if ((ticks, length) != (request.ExpectedLastWriteTicks, request.ExpectedByteLength))
            return new Outcome(null, PageHashErrors.SourceChanged);

        return new Outcome(new PageHashResult
        {
            JobId = request.JobId,
            PageCount = pageCount,
            Pages = hashed,
            Solid = solid,
            ObservedLastWriteTicks = ticks,
            ObservedByteLength = length,
            ElapsedMs = (int)sw.ElapsedMilliseconds,
        }, null);
    }

    /// <summary>One page's entry: its hash, or a page-level error (too large / not decodable / not readable). Never throws.</summary>
    private static PageHashEntry HashPage(int ordinal, byte[]? bytes, int maxDimension)
    {
        if (bytes is null)
            return new PageHashEntry { Ordinal = ordinal, Error = PageHashErrors.TooLarge };
        var image = ImageHasher.Hash(bytes, maxDimension, reducedDecode: true);
        return image.Error is null
            ? new PageHashEntry { Ordinal = ordinal, Hash = image.Hash }
            : new PageHashEntry
            {
                Ordinal = ordinal,
                Error = image.Error == ImageHashErrors.TooLarge ? PageHashErrors.TooLarge : PageHashErrors.DecodeFailed,
            };
    }

    private static void CheckDeadline(PageHashRequest request, ref bool deadlineHit)
    {
        if (DateTimeOffset.UtcNow <= request.Deadline)
            return;
        deadlineHit = true;
        throw new OperationCanceledException();
    }

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

    internal static (long LastWriteTicks, long ByteLength) StampOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0, 0);
    }
}
