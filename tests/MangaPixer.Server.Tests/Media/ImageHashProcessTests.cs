namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Images;
using com.lifepixer.mangapixer.MediaWorker.Protocol;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch.Golden;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Process tests for the protocol v4 cover hash (1.28.0): real spawned worker processes behind the server's
/// <see cref="MediaWorkerPool"/> and the production <see cref="WorkerCoverHasher"/>. The hash crosses the process
/// boundary unchanged (the full unsigned range), errors come back as codes, and a v3 peer is refused loudly.
/// </summary>
[Trait("Category", "Process")]
public sealed class ImageHashProcessTests(WorkerProcessFixture fixture) : IClassFixture<WorkerProcessFixture>
{
    private async Task<MediaWorkerPool> StartPoolAsync()
    {
        var options = fixture.CreatePoolOptions();
        var pool = new MediaWorkerPool(options, new JobScheduler(options), new ScratchWorkspaceManager(fixture.ScratchRoot),
            NullLogger<MediaWorkerPool>.Instance, NullLoggerFactory.Instance);
        await pool.StartAsync();
        return pool;
    }

    private string Fixture(string name, byte[] bytes)
    {
        var path = Path.Combine(fixture.FixtureDir, "imghash-" + Guid.NewGuid().ToString("N")[..6] + "-" + name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task ImageHash_ARecordedCover_IsHashedByTheWorker_ExactlyAsTheWorkerCodeHashesIt()
    {
        var bytes = GoldenFixtures.Image("cover.i523282.jpg")!;
        var path = Fixture("cover.jpg", bytes);
        var pool = await StartPoolAsync();
        try
        {
            var outcome = await pool.HashImageAsync(path);
            var hasher = await new WorkerCoverHasher(pool).HashFileAsync(path, CancellationToken.None);

            Assert.True(outcome.Success, outcome.ErrorType);
            Assert.Equal(ImageHasher.Hash(bytes, ImageHashLimits.MaxDimension).Hash, outcome.Hash);
            Assert.Equal(outcome.Hash, hasher);
            Assert.True(File.Exists(path)); // the worker reads, never deletes or writes
        }
        finally
        {
            await pool.StopAsync();
            await pool.DisposeAsync();
        }
    }

    [Fact]
    public async Task ImageHash_MissingAndUndecodableFiles_AreErrorCodes_AndTheHasherAnswersNull()
    {
        var text = Fixture("text.jpg", "definitely not an image"u8.ToArray());
        var pool = await StartPoolAsync();
        try
        {
            Assert.Equal(ImageHashErrors.Missing, (await pool.HashImageAsync(Path.Combine(fixture.FixtureDir, "absent.jpg"))).ErrorType);
            Assert.Equal(ImageHashErrors.DecodeFailed, (await pool.HashImageAsync(text)).ErrorType);
            Assert.Null(await new WorkerCoverHasher(pool).HashFileAsync(text, CancellationToken.None));
        }
        finally
        {
            await pool.StopAsync();
            await pool.DisposeAsync();
        }
    }

    [Fact]
    public async Task ImageHash_FromAV3Peer_IsRefusedWithAProtocolMismatch()
    {
        await using var supervisor = new WorkerSupervisor(fixture.WorkerExePath, fixture.WorkerArguments, fixture.CreatePoolOptions(),
            NullLogger<WorkerSupervisor>.Instance);
        await supervisor.StartAsync();
        var tcs = new TaskCompletionSource<WorkerEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.OnMessageReceived += envelope =>
        {
            if (envelope.CorrelationId == "v3-peer") tcs.TrySetResult(envelope);
            return Task.CompletedTask;
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var readLoop = supervisor.ReadMessagesAsync(cts.Token);

        var current = WorkerProtocolFraming.CreateEnvelope("image_hash", "v3-peer", new ImageHashRequest { JobId = "v3-peer", ImagePath = "/nonexistent" });
        await supervisor.SendMessageAsync(current with { ProtocolVersion = 3 });

        var reply = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("protocol_version_mismatch", WorkerProtocolFraming.GetPayload<AnalyzeError>(reply)!.ErrorType);
        cts.Cancel();
        try { await readLoop; } catch (OperationCanceledException) { }
    }
}
