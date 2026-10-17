namespace com.lifepixer.mangapixer.Tests.MediaWorker.Archives;

using System.Diagnostics;
using System.IO.Compression;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Archives;
using com.lifepixer.mangapixer.MediaWorker.Images;
using ImageMagick;
using ImageMagick.Drawing;
using Xunit;

/// <summary>
/// The <c>page_hash</c> op (worker protocol v6, 1.44.0 volume contents scan): pages in analysis order, chunks, limits, stamp checks,
/// page-level errors, solid archives in one pass - and the source archive is never written (bytes and modification time unchanged on
/// a read-only file).
/// </summary>
public sealed class PageHashOperationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mp-pagehash-" + Guid.NewGuid().ToString("N"));

    public PageHashOperationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A synthetic page: a seeded pattern of dark blocks on white (distinct seeds give distinct hashes).</summary>
    internal static byte[] Page(int seed, MagickFormat format = MagickFormat.Jpeg)
    {
        using var image = new MagickImage(MagickColors.White, 300, 440);
        var random = new Random(seed);
        var draw = new Drawables();
        for (var i = 0; i < 12; i++)
        {
            var gray = (byte)random.Next(0, 160);
            draw.FillColor(new MagickColor(gray, gray, gray))
                .Rectangle(random.Next(0, 250), random.Next(0, 400), random.Next(0, 300), random.Next(0, 440));
        }
        draw.Draw(image);
        image.Format = format;
        image.Quality = 85;
        return image.ToByteArray();
    }

    private string Zip(string name, params (string Entry, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (entry, bytes) in entries)
            {
                using var s = zip.CreateEntry(entry).Open();
                s.Write(bytes);
            }
        }
        return path;
    }

    private static PageHashRequest Request(string path, int from = 0, int count = 0) => new()
    {
        JobId = "j",
        ArchivePath = path,
        ExpectedLastWriteTicks = File.GetLastWriteTimeUtc(path).Ticks,
        ExpectedByteLength = new FileInfo(path).Length,
        Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
        FromOrdinal = from,
        Count = count,
    };

    [Fact]
    public async Task Hashes_EveryImagePage_InAnalysisOrder_WithTheReducedDecodeHash()
    {
        // Entry order in the zip is scrambled; analysis orders naturally (p2 before p10) and drops non-images.
        var pages = Enumerable.Range(1, 12).Select(i => Page(i)).ToArray();
        var path = Zip("v.cbz",
            [.. Enumerable.Range(1, 12).Reverse().Select(i => ($"p{i}.jpg", pages[i - 1])), ("ComicInfo.xml", "<x/>"u8.ToArray())]);

        var outcome = await PageHashOperation.RunAsync(Request(path), CancellationToken.None);

        Assert.Null(outcome.Error);
        var result = outcome.Result!;
        Assert.Equal(12, result.PageCount);
        Assert.False(result.Solid);
        Assert.Equal(Enumerable.Range(0, 12), result.Pages.Select(p => p.Ordinal));
        for (var i = 0; i < 12; i++)
            Assert.Equal(ImageHasher.Hash(pages[i], PageHashLimits.MaxDimension, reducedDecode: true).Hash, result.Pages[i].Hash);
        Assert.Equal(12, result.Pages.Select(p => p.Hash).Distinct().Count());
    }

    [Fact]
    public async Task A_Chunk_HashesOnlyItsRange_AndTheCountReportsTheWholeArchive()
    {
        var path = Zip("v.cbz", [.. Enumerable.Range(1, 10).Select(i => ($"p{i:00}.png", Page(i, MagickFormat.Png)))]);

        var result = (await PageHashOperation.RunAsync(Request(path, from: 4, count: 3), CancellationToken.None)).Result!;
        var tail = (await PageHashOperation.RunAsync(Request(path, from: 8, count: 48), CancellationToken.None)).Result!;

        Assert.Equal(10, result.PageCount);
        Assert.Equal([4, 5, 6], result.Pages.Select(p => p.Ordinal));
        Assert.Equal([8, 9], tail.Pages.Select(p => p.Ordinal));
    }

    [Fact]
    public async Task A_PageThatIsNotAnImage_IsAPageError_NotAFailedOp()
    {
        var path = Zip("v.cbz", ("p1.jpg", Page(1)), ("p2.jpg", "not an image"u8.ToArray()), ("p3.jpg", Page(3)));

        var result = (await PageHashOperation.RunAsync(Request(path), CancellationToken.None)).Result!;

        Assert.NotNull(result.Pages[0].Hash);
        Assert.Null(result.Pages[1].Hash);
        Assert.Equal(PageHashErrors.DecodeFailed, result.Pages[1].Error);
        Assert.NotNull(result.Pages[2].Hash);
    }

    [Fact]
    public async Task Limits_AreClamped_AndRefused()
    {
        var path = Zip("v.cbz", [.. Enumerable.Range(1, 3).Select(i => ($"p{i}.jpg", Page(i)))]);

        var tooMany = await PageHashOperation.RunAsync(Request(path) with { MaxPages = 2 }, CancellationToken.None);
        var tooLargePage = (await PageHashOperation.RunAsync(Request(path) with { MaxPageBytes = 10 }, CancellationToken.None)).Result!;
        var clamped = await PageHashOperation.RunAsync(Request(path) with { MaxPages = int.MaxValue }, CancellationToken.None);
        var invalid = await PageHashOperation.RunAsync(Request(path, from: -1), CancellationToken.None);

        Assert.Equal(PageHashErrors.TooManyPages, tooMany.Error);
        Assert.All(tooLargePage.Pages, p => Assert.Equal(PageHashErrors.TooLarge, p.Error));
        Assert.Null(clamped.Error);
        Assert.Equal(PageHashErrors.InvalidRequest, invalid.Error);
    }

    [Fact]
    public async Task A_ChangedOrMissingSource_IsRefused()
    {
        var path = Zip("v.cbz", ("p1.jpg", Page(1)));

        var changed = await PageHashOperation.RunAsync(Request(path) with { ExpectedByteLength = 1 }, CancellationToken.None);
        var missing = await PageHashOperation.RunAsync(Request(path) with { ArchivePath = Path.Combine(_dir, "gone.cbz") }, CancellationToken.None);
        var late = await PageHashOperation.RunAsync(Request(path) with { Deadline = DateTimeOffset.UtcNow.AddMinutes(-1) }, CancellationToken.None);

        Assert.Equal(PageHashErrors.SourceChanged, changed.Error);
        Assert.Equal(PageHashErrors.Missing, missing.Error);
        Assert.Equal(PageHashErrors.Deadline, late.Error);
    }

    [Fact]
    public async Task The_SourceArchive_IsOnlyRead_BytesAndModificationTimeUnchanged_OnAReadOnlyFile()
    {
        var path = Zip("v.cbz", [.. Enumerable.Range(1, 5).Select(i => ($"p{i}.jpg", Page(i)))]);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var before = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));
        var entriesBefore = Directory.GetFileSystemEntries(_dir).Order().ToArray();

        var outcome = await PageHashOperation.RunAsync(Request(path), CancellationToken.None);

        Assert.Null(outcome.Error);
        Assert.Equal(5, outcome.Result!.Pages.Count(p => p.Hash is not null));
        Assert.Equal(before.Item1, File.ReadAllBytes(path));
        Assert.Equal(before.Item2, File.GetLastWriteTimeUtc(path));
        Assert.Equal(entriesBefore, Directory.GetFileSystemEntries(_dir).Order().ToArray());
    }

    [Fact]
    public async Task A_SolidArchive_IsReadFrontToBackOnce_EveryPageFromTheRequestedOrdinal()
    {
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        var pages = Enumerable.Range(1, 6).Select(i => Page(i)).ToArray();
        for (var i = 0; i < 6; i++)
            File.WriteAllBytes(Path.Combine(src, $"p{i + 1}.jpg"), pages[i]);
        var path = Path.Combine(_dir, "v.cb7");
        if (!SevenZip($"a -t7z -ms=on \"{path}\" \"{src}/*\""))
            return; // no 7z tool in this environment (the verify container installs p7zip-full)

        var result = (await PageHashOperation.RunAsync(Request(path, from: 2, count: 1), CancellationToken.None)).Result!;

        Assert.True(result.Solid);
        Assert.Equal([2, 3, 4, 5], result.Pages.Select(p => p.Ordinal));
        Assert.Equal(ImageHasher.Hash(pages[2], PageHashLimits.MaxDimension, reducedDecode: true).Hash, result.Pages[0].Hash);
    }

    private static bool SevenZip(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "7z",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null)
                return false;
            p.WaitForExit(30_000);
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
