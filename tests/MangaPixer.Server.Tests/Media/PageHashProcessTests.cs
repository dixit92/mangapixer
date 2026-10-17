namespace com.lifepixer.mangapixer.Tests.Server.Media;

using System.IO.Compression;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Images;
using com.lifepixer.mangapixer.Server.Media;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Process tests for the protocol v6 page hashes (1.44.0 volume contents scan): real spawned worker processes behind the server's
/// <see cref="MediaWorkerPool"/>. The hashes cross the process boundary unchanged, a chunk returns its range, errors come back as codes,
/// and the source archive is only read (bytes and modification time unchanged).
/// </summary>
[Trait("Category", "Process")]
public sealed class PageHashProcessTests(WorkerProcessFixture fixture) : IClassFixture<WorkerProcessFixture>
{
    private async Task<MediaWorkerPool> StartPoolAsync()
    {
        var options = fixture.CreatePoolOptions();
        var pool = new MediaWorkerPool(options, new JobScheduler(options), new ScratchWorkspaceManager(fixture.ScratchRoot),
            NullLogger<MediaWorkerPool>.Instance, NullLoggerFactory.Instance);
        await pool.StartAsync();
        return pool;
    }

    private static byte[] Page(int seed)
    {
        using var image = new MagickImage(MagickColors.White, 240, 360);
        var random = new Random(seed);
        var draw = new Drawables();
        for (var i = 0; i < 10; i++)
        {
            var gray = (byte)random.Next(0, 160);
            draw.FillColor(new MagickColor(gray, gray, gray)).Rectangle(random.Next(0, 200), random.Next(0, 320), random.Next(0, 240), random.Next(0, 360));
        }
        draw.Draw(image);
        image.Format = MagickFormat.Jpeg;
        return image.ToByteArray();
    }

    private string Archive(int pages, out byte[][] bytes)
    {
        bytes = [.. Enumerable.Range(1, pages).Select(Page)];
        var path = Path.Combine(fixture.FixtureDir, "pagehash-" + Guid.NewGuid().ToString("N")[..6] + ".cbz");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            for (var i = 0; i < pages; i++)
            {
                using var s = zip.CreateEntry($"{i + 1:000}.jpg").Open();
                s.Write(bytes[i]);
            }
        }
        File.SetLastWriteTimeUtc(path, new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        return path;
    }

    private static PageHashRequest Request(string path, int from, int count) => new()
    {
        JobId = "ignored",
        ArchivePath = path,
        ExpectedLastWriteTicks = File.GetLastWriteTimeUtc(path).Ticks,
        ExpectedByteLength = new FileInfo(path).Length,
        Deadline = DateTimeOffset.UnixEpoch, // the pool sets its own
        FromOrdinal = from,
        Count = count,
    };

    [Fact]
    public async Task PageHash_AChunk_IsHashedByTheWorker_ExactlyAsTheWorkerCodeHashesIt_AndTheSourceIsOnlyRead()
    {
        var path = Archive(7, out var pages);
        var before = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));
        var pool = await StartPoolAsync();
        try
        {
            var first = await pool.HashPagesAsync(Request(path, 0, 4));
            var rest = await pool.HashPagesAsync(Request(path, 4, 4));

            Assert.True(first.Success, first.ErrorType);
            Assert.Equal(7, first.Result!.PageCount);
            Assert.Equal([0, 1, 2, 3], first.Result.Pages.Select(p => p.Ordinal));
            Assert.Equal([4, 5, 6], rest.Result!.Pages.Select(p => p.Ordinal));
            var all = first.Result.Pages.Concat(rest.Result.Pages).ToList();
            for (var i = 0; i < 7; i++)
                Assert.Equal(ImageHasher.Hash(pages[i], PageHashLimits.MaxDimension, reducedDecode: true).Hash, all[i].Hash);
            Assert.Equal(before.Item1, File.ReadAllBytes(path));
            Assert.Equal(before.Item2, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            await pool.StopAsync();
            await pool.DisposeAsync();
        }
    }

    [Fact]
    public async Task PageHash_AChangedOrMissingArchive_IsAnErrorCode()
    {
        var path = Archive(2, out _);
        var pool = await StartPoolAsync();
        try
        {
            var changed = await pool.HashPagesAsync(Request(path, 0, 0) with { ExpectedByteLength = 3 });
            var missing = await pool.HashPagesAsync(Request(path, 0, 0) with { ArchivePath = Path.Combine(fixture.FixtureDir, "absent.cbz") });

            Assert.Equal(PageHashErrors.SourceChanged, changed.ErrorType);
            Assert.Equal(PageHashErrors.Missing, missing.ErrorType);
        }
        finally
        {
            await pool.StopAsync();
            await pool.DisposeAsync();
        }
    }
}
