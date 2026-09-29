namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Images;
using com.lifepixer.mangapixer.MediaWorker.Protocol;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch.Golden;
using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Process tests for the protocol v5 cover render (1.29.0): real spawned worker processes behind the server's
/// <see cref="MediaWorkerPool"/> and the production <see cref="WorkerCoverRenderer"/>. A server-owned image is re-encoded
/// and hashed, an archive page spread is cropped to one half, the archive checks of <c>extract</c> apply (stamp, entry),
/// errors come back as codes, and a v4 peer is refused loudly. Drawn images only.
/// </summary>
[Trait("Category", "Process")]
public sealed class CoverRenderProcessTests(WorkerProcessFixture fixture) : IClassFixture<WorkerProcessFixture>
{
    private async Task<MediaWorkerPool> StartPoolAsync()
    {
        var options = fixture.CreatePoolOptions();
        var pool = new MediaWorkerPool(options, new JobScheduler(options), new ScratchWorkspaceManager(fixture.ScratchRoot),
            NullLogger<MediaWorkerPool>.Instance, NullLoggerFactory.Instance);
        await pool.StartAsync();
        return pool;
    }

    private string Unique(string name) => Path.Combine(fixture.FixtureDir, "cover-" + Guid.NewGuid().ToString("N")[..6] + "-" + name);

    private string Output() => Path.Combine(fixture.ScratchRoot, "cover-out-" + Guid.NewGuid().ToString("N")[..8] + ".webp");

    /// <summary>A jacket spread page: drawn cover (seed 7) on the left, a spine, drawn cover (seed 8) on the right.</summary>
    private static byte[] SpreadPng()
    {
        using var canvas = new MagickImage(MagickColors.Black, SyntheticCovers.Width * 2 + 30, SyntheticCovers.Height);
        using var left = SyntheticCovers.Draw(7);
        using var right = SyntheticCovers.Draw(8);
        canvas.Composite(left, 0, 0, CompositeOperator.Over);
        canvas.Composite(right, (int)SyntheticCovers.Width + 30, 0, CompositeOperator.Over);
        canvas.Format = MagickFormat.Png;
        return canvas.ToByteArray();
    }

    private string ZipWith(string entryName, byte[] bytes)
    {
        var path = Unique("book.cbz");
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        using var s = zip.CreateEntry(entryName).Open();
        s.Write(bytes);
        return path;
    }

    private static CoverRenderRequest ArchiveRequest(string zip, string entry, string output, string crop)
    {
        var fi = new FileInfo(zip);
        return new CoverRenderRequest
        {
            JobId = "ignored",
            Source = CoverRenderSources.Archive,
            ArchivePath = zip,
            SourceEntryKey = entry,
            ExpectedLastWriteTicks = fi.LastWriteTimeUtc.Ticks,
            ExpectedByteLength = fi.Length,
            CropSide = crop,
            // The drawn covers are exactly 2:3, so the crop takes exactly one cover.
            CropAspect = (double)SyntheticCovers.Width / SyntheticCovers.Height,
            OutputPath = output,
        };
    }

    [Fact]
    public async Task CoverRender_AServerOwnedImage_IsReEncodedToTheThumbnailVariant_AndHashedAsImageHashWould()
    {
        var bytes = SyntheticCovers.Png(5);
        var image = Unique("provider.png");
        File.WriteAllBytes(image, bytes);
        var output = Output();
        var pool = await StartPoolAsync();
        try
        {
            var outcome = await new WorkerCoverRenderer(pool).RenderAsync(new CoverRenderRequest
            {
                JobId = "ignored",
                Source = CoverRenderSources.Image,
                ImagePath = image,
                OutputPath = output,
                MaxDimension = 150,
            }, CancellationToken.None);

            Assert.True(outcome.Success, outcome.ErrorType);
            Assert.Equal(output, outcome.OutputPath);
            Assert.Equal((100, 150), (outcome.Width, outcome.Height));
            Assert.Equal(((int)SyntheticCovers.Width, (int)SyntheticCovers.Height), (outcome.SourceWidth, outcome.SourceHeight));
            Assert.Equal(MagickFormat.WebP, new MagickImageInfo(output).Format);
            Assert.Equal(ImageHasher.Hash(File.ReadAllBytes(output), ImageHashLimits.MaxDimension).Hash, outcome.Hash);
            Assert.True(File.Exists(image)); // the worker reads the input, never deletes it
        }
        finally
        {
            await pool.StopAsync();
            await pool.DisposeAsync();
        }
    }

    [Fact]
    public async Task CoverRender_AnArchiveSpread_CroppedRight_IsTheRightHandCover()
    {
        var zip = ZipWith("page001.png", SpreadPng());
        var pool = await StartPoolAsync();
        try
        {
            var right = await pool.RenderCoverAsync(ArchiveRequest(zip, "page001.png", Output(), CoverCropSides.Right));
            var left = await pool.RenderCoverAsync(ArchiveRequest(zip, "page001.png", Output(), CoverCropSides.Left));

            Assert.True(right.Success, right.ErrorType);
            Assert.True(left.Success, left.ErrorType);
            Assert.Equal(((int)SyntheticCovers.Width * 2 + 30, (int)SyntheticCovers.Height), (right.SourceWidth, right.SourceHeight));
            var coverRight = ImageHasher.Hash(SyntheticCovers.Png(8), ImageHashLimits.MaxDimension).Hash;
            var coverLeft = ImageHasher.Hash(SyntheticCovers.Png(7), ImageHashLimits.MaxDimension).Hash;
            Assert.True(CoverHash.Distance(right.Hash!.Value, coverRight) <= CoverHash.SameMaxDistance);
            Assert.True(CoverHash.Distance(left.Hash!.Value, coverLeft) <= CoverHash.SameMaxDistance);
            Assert.True(CoverHash.Distance(right.Hash.Value, coverLeft) >= CoverHash.DifferentMinDistance);
        }
        finally
        {
            await pool.StopAsync();
            await pool.DisposeAsync();
        }
    }

    [Fact]
    public async Task CoverRender_ArchiveAndFileProblems_AreErrorCodes_AndLeaveNoOutput()
    {
        var zip = ZipWith("page001.png", SyntheticCovers.Png(9));
        var text = Unique("text.jpg");
        File.WriteAllText(text, "definitely not an image");
        var pool = await StartPoolAsync();
        try
        {
            var changed = ArchiveRequest(zip, "page001.png", Output(), CoverCropSides.None) with { ExpectedByteLength = 1 };
            Assert.Equal(CoverRenderErrors.SourceChanged, (await pool.RenderCoverAsync(changed)).ErrorType);

            var noEntry = ArchiveRequest(zip, "page999.png", Output(), CoverCropSides.None);
            Assert.Equal(CoverRenderErrors.PageNotFound, (await pool.RenderCoverAsync(noEntry)).ErrorType);
            Assert.False(File.Exists(noEntry.OutputPath));

            var missing = new CoverRenderRequest { JobId = "x", Source = CoverRenderSources.Image, ImagePath = Unique("absent.png"), OutputPath = Output() };
            Assert.Equal(CoverRenderErrors.Missing, (await pool.RenderCoverAsync(missing)).ErrorType);

            var undecodable = new CoverRenderRequest { JobId = "x", Source = CoverRenderSources.Image, ImagePath = text, OutputPath = Output() };
            Assert.Equal(CoverRenderErrors.DecodeFailed, (await pool.RenderCoverAsync(undecodable)).ErrorType);
            Assert.False(File.Exists(undecodable.OutputPath));

            var unknownSource = new CoverRenderRequest { JobId = "x", Source = "url", OutputPath = Output() };
            Assert.Equal(CoverRenderErrors.InvalidRequest, (await pool.RenderCoverAsync(unknownSource)).ErrorType);
        }
        finally
        {
            await pool.StopAsync();
            await pool.DisposeAsync();
        }
    }

    [Fact]
    public async Task CoverRender_FromAV4Peer_IsRefusedWithAProtocolMismatch()
    {
        await using var supervisor = new WorkerSupervisor(fixture.WorkerExePath, fixture.WorkerArguments, fixture.CreatePoolOptions(),
            NullLogger<WorkerSupervisor>.Instance);
        await supervisor.StartAsync();
        var tcs = new TaskCompletionSource<WorkerEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.OnMessageReceived += envelope =>
        {
            if (envelope.CorrelationId == "v4-peer") tcs.TrySetResult(envelope);
            return Task.CompletedTask;
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var readLoop = supervisor.ReadMessagesAsync(cts.Token);

        var current = WorkerProtocolFraming.CreateEnvelope("cover_render", "v4-peer", new CoverRenderRequest
        {
            JobId = "v4-peer",
            Source = CoverRenderSources.Image,
            ImagePath = "/nonexistent",
            OutputPath = "/nonexistent.webp",
        });
        await supervisor.SendMessageAsync(current with { ProtocolVersion = 4 });

        var reply = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("protocol_version_mismatch", WorkerProtocolFraming.GetPayload<AnalyzeError>(reply)!.ErrorType);
        cts.Cancel();
        try { await readLoop; } catch (OperationCanceledException) { }
    }
}
