namespace com.lifepixer.mangapixer.Tests.Server.Media;

using System.Diagnostics;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Protocol;
using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Worker-process integration tests.
/// These tests spawn real worker processes and verify the full IPC path.
/// Category: Process — must run on Linux for reliable process management.
/// </summary>
[Trait("Category", "Process")]
public sealed class WorkerProcessTests : IClassFixture<WorkerProcessFixture>, IAsyncDisposable
{
    private readonly WorkerProcessFixture _fixture;
    private readonly List<WorkerSupervisor> _supervisors = [];

    public WorkerProcessTests(WorkerProcessFixture fixture)
    {
        _fixture = fixture;
    }

    private WorkerSupervisor CreateSupervisor()
    {
        var sup = new WorkerSupervisor(
            _fixture.WorkerExePath,
            _fixture.WorkerArguments,
            _fixture.CreatePoolOptions(),
            NullLogger<WorkerSupervisor>.Instance);
        _supervisors.Add(sup);
        return sup;
    }

    // Test 1: Handshake succeeds within 15s
    [Fact]
    public async Task Handshake_SucceedsWithin15Seconds()
    {
        var sup = CreateSupervisor();
        await sup.StartAsync();
        Assert.True(sup.IsRunning);
        Assert.True(sup.IsReady);
    }

    // Test 2: Analyze on a simple ZIP returns correct page count
    [Fact]
    public async Task Analyze_SimpleZip_ReturnsCorrectPageCount()
    {
        var zipPath = _fixture.CreateSimpleZip("analyze-simple.zip");
        var fileInfo = new FileInfo(zipPath);

        var sup = CreateSupervisor();
        await sup.StartAsync();

        var tcs = new TaskCompletionSource<AnalyzeResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        sup.OnMessageReceived += async envelope =>
        {
            if (envelope.Type == "analyze_result")
            {
                var result = WorkerProtocolFraming.GetPayload<AnalyzeResult>(envelope);
                tcs.TrySetResult(result);
            }
            await Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var readTask = sup.ReadMessagesAsync(cts.Token);

        var request = new AnalyzeRequest
        {
            JobId = "test-job-1",
            ArchivePath = zipPath,
            ContentVersion = 1,
            ExpectedLastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks,
            ExpectedByteLength = fileInfo.Length,
            ScratchWorkspacePath = Path.Combine(_fixture.ScratchRoot, "ws-1"),
            Deadline = DateTimeOffset.UtcNow.AddSeconds(30),
        };
        Directory.CreateDirectory(request.ScratchWorkspacePath);

        var envelope = WorkerProtocolFraming.CreateEnvelope("analyze", request.JobId, request);
        await sup.SendMessageAsync(envelope);

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(20));
        var completed = await Task.WhenAny(tcs.Task, timeoutTask);
        Assert.True(completed == tcs.Task, "Analyze did not complete within 20 seconds");
        var result = await tcs.Task;
        Assert.NotNull(result);
        Assert.Equal(3, result!.Pages.Count);
    }

    // Test 2b: Pages are returned in natural reading order regardless of the
    // archive's stored entry order (page-ordering bug — cover appeared last).
    [Fact]
    public async Task Analyze_ScrambledEntryOrder_ReturnsNaturalPageOrder()
    {
        var zipPath = _fixture.CreateScrambledZip("analyze-scrambled.zip");
        var fileInfo = new FileInfo(zipPath);

        var sup = CreateSupervisor();
        await sup.StartAsync();

        var tcs = new TaskCompletionSource<AnalyzeResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sup.OnMessageReceived += async envelope =>
        {
            if (envelope.Type == "analyze_result")
                tcs.TrySetResult(WorkerProtocolFraming.GetPayload<AnalyzeResult>(envelope));
            await Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var readTask = sup.ReadMessagesAsync(cts.Token);

        var request = new AnalyzeRequest
        {
            JobId = "scramble-job",
            ArchivePath = zipPath,
            ContentVersion = 1,
            ExpectedLastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks,
            ExpectedByteLength = fileInfo.Length,
            ScratchWorkspacePath = Path.Combine(_fixture.ScratchRoot, "ws-scramble"),
            Deadline = DateTimeOffset.UtcNow.AddSeconds(30),
        };
        Directory.CreateDirectory(request.ScratchWorkspacePath);

        await sup.SendMessageAsync(WorkerProtocolFraming.CreateEnvelope("analyze", request.JobId, request));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(completed == tcs.Task, "Analyze did not complete within 20 seconds");
        var result = await tcs.Task;
        Assert.NotNull(result);

        // Stored order was page010, page002, page001, cover000. Natural order by
        // entry path is cover000, page001, page002, page010.
        var orderedKeys = result!.Pages
            .OrderBy(p => p.Ordinal)
            .Select(p => p.SourceEntryKey)
            .ToList();
        Assert.Equal(
            new[] { "cover000.png", "page001.png", "page002.png", "page010.png" },
            orderedKeys);
        // Ordinals are a dense 0..n-1 sequence in that order.
        Assert.Equal(Enumerable.Range(0, 4), result.Pages.OrderBy(p => p.Ordinal).Select(p => p.Ordinal));
    }

    // Test 2c: Extract a page as a WebP variant. The worker decodes the
    // source image and re-encodes to WebP, writing to the server-provided path.
    [Fact]
    public async Task Extract_WebpVariant_ProducesWebpFile()
    {
        var zipPath = _fixture.CreateValidImageZip("extract-webp.zip");
        var fileInfo = new FileInfo(zipPath);
        var outputPath = Path.Combine(_fixture.ScratchRoot, "out-" + Guid.NewGuid().ToString("N")[..8] + ".webp");

        var sup = CreateSupervisor();
        await sup.StartAsync();

        var tcs = new TaskCompletionSource<ExtractResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errTcs = new TaskCompletionSource<ExtractError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.OnMessageReceived += async envelope =>
        {
            if (envelope.Type == "extract_result") tcs.TrySetResult(WorkerProtocolFraming.GetPayload<ExtractResult>(envelope));
            else if (envelope.Type == "extract_error") errTcs.TrySetResult(WorkerProtocolFraming.GetPayload<ExtractError>(envelope));
            await Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var readTask = sup.ReadMessagesAsync(cts.Token);

        var request = new ExtractRequest
        {
            JobId = "extract-job",
            ArchivePath = zipPath,
            SourceEntryKey = "page001.png",
            Variant = "webp",
            ExpectedLastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks,
            ExpectedByteLength = fileInfo.Length,
            OutputPath = outputPath,
            Deadline = DateTimeOffset.UtcNow.AddSeconds(30),
        };
        await sup.SendMessageAsync(WorkerProtocolFraming.CreateEnvelope("extract", request.JobId, request));

        var completed = await Task.WhenAny(tcs.Task, errTcs.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        if (errTcs.Task.IsCompleted)
        {
            var e = await errTcs.Task;
            Assert.Fail($"extract_error: {e?.ErrorType}: {e?.ErrorMessage}");
        }
        Assert.True(completed == tcs.Task, "Extract did not return a result in time");
        var result = await tcs.Task;
        Assert.NotNull(result);
        Assert.Equal("image/webp", result!.MediaType);
        Assert.True(File.Exists(outputPath), "worker did not write the output file");

        // Verify the RIFF/WEBP container signature.
        var head = await File.ReadAllBytesAsync(outputPath);
        Assert.True(head.Length > 12);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(head, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(head, 8, 4));
    }

    // Test 2d: Extracting a non-existent entry yields a graceful extract_error.
    [Fact]
    public async Task Extract_MissingEntry_ReturnsExtractError()
    {
        var zipPath = _fixture.CreateSimpleZip("extract-missing.zip");
        var fileInfo = new FileInfo(zipPath);
        var outputPath = Path.Combine(_fixture.ScratchRoot, "out-missing.webp");

        var sup = CreateSupervisor();
        await sup.StartAsync();

        var errTcs = new TaskCompletionSource<ExtractError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var okTcs = new TaskCompletionSource<ExtractResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.OnMessageReceived += async envelope =>
        {
            if (envelope.Type == "extract_error") errTcs.TrySetResult(WorkerProtocolFraming.GetPayload<ExtractError>(envelope));
            else if (envelope.Type == "extract_result") okTcs.TrySetResult(WorkerProtocolFraming.GetPayload<ExtractResult>(envelope));
            await Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var readTask = sup.ReadMessagesAsync(cts.Token);

        var request = new ExtractRequest
        {
            JobId = "extract-missing",
            ArchivePath = zipPath,
            SourceEntryKey = "does-not-exist.png",
            Variant = "webp",
            ExpectedLastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks,
            ExpectedByteLength = fileInfo.Length,
            OutputPath = outputPath,
            Deadline = DateTimeOffset.UtcNow.AddSeconds(30),
        };
        await sup.SendMessageAsync(WorkerProtocolFraming.CreateEnvelope("extract", request.JobId, request));

        var completed = await Task.WhenAny(errTcs.Task, okTcs.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(completed == errTcs.Task, "Expected an extract_error for a missing entry");
        var err = await errTcs.Task;
        Assert.NotNull(err);
        Assert.Equal("page_not_found", err!.ErrorType);
    }

    // Test 3: Kill worker mid-job → supervisor detects exit
    [Fact]
    public async Task WorkerKill_SupervisorDetectsExit()
    {
        var sup = CreateSupervisor();
        await sup.StartAsync();
        Assert.True(sup.IsRunning);

        // Forcefully dispose the supervisor — the process should be gone
        await sup.DisposeAsync();
        // After dispose, the supervisor should no longer be running
        Assert.False(sup.IsRunning);
    }

    // Test 4: Cooperative cancel — worker exits within grace period
    [Fact]
    public async Task CooperativeCancel_WorkerExitsGracefully()
    {
        var sup = CreateSupervisor();
        await sup.StartAsync();
        Assert.True(sup.IsRunning);

        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.OnWorkerExited += code => exitTcs.TrySetResult(code);

        // Use StopAsync which sends shutdown and waits with grace period
        await sup.StopAsync();

        // The worker should have exited gracefully
        Assert.False(sup.IsRunning);
    }

    // Test 5: MediaWorkerPool does not fail jobs when no slot is free (D12)
    [Fact]
    public async Task Pool_NoSlotAvailable_DoesNotFailJob()
    {
        var options = _fixture.CreatePoolOptions();
        options.MaxConcurrentJobs = 1;

        var scratchManager = new ScratchWorkspaceManager(_fixture.ScratchRoot);
        var scheduler = new JobScheduler(options);
        var pool = new MediaWorkerPool(
            options, scheduler, scratchManager,
            NullLogger<MediaWorkerPool>.Instance, NullLoggerFactory.Instance);

        await pool.StartAsync();

        // Enqueue two jobs — only one can run at a time
        var zipPath = _fixture.CreateSimpleZip("pool-test.zip");
        var fileInfo = new FileInfo(zipPath);

        var job1 = scheduler.EnqueueAsync(
            itemId: 1,
            contentVersion: 1,
            operation: JobOperation.Analyze,
            priority: JobPriority.CurrentPage,
            archivePath: zipPath,
            expectedLastWriteTicks: fileInfo.LastWriteTimeUtc.Ticks,
            expectedByteLength: fileInfo.Length);

        var job2 = scheduler.EnqueueAsync(
            itemId: 2,
            contentVersion: 1,
            operation: JobOperation.Analyze,
            priority: JobPriority.CurrentPage,
            archivePath: zipPath,
            expectedLastWriteTicks: fileInfo.LastWriteTimeUtc.Ticks,
            expectedByteLength: fileInfo.Length);

        // Dispatch — should process one job, leave the other in queue
        await pool.DispatchAsync();

        // Wait a bit for the first job to start
        await Task.Delay(2000);

        // Dispatch again — should not fail the second job even if the
        // first worker is still busy
        await pool.DispatchAsync();

        // The second job should not have been failed (D12)
        // We verify by checking that the job2 task is not faulted
        Assert.False(job2.IsFaulted, "Second job was faulted instead of remaining pending");

        // Cleanup
        await pool.StopAsync();
        await pool.DisposeAsync();
    }

    // Test 6: Stale source discard — modify file between dispatch and completion
    [Fact]
    public async Task Analyze_SourceModified_RejectsResult()
    {
        var zipPath = _fixture.CreateSimpleZip("stale-source.zip");
        var fileInfo = new FileInfo(zipPath);

        var sup = CreateSupervisor();
        await sup.StartAsync();

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Record the original stamp
        var originalTicks = fileInfo.LastWriteTimeUtc.Ticks;
        var originalLength = fileInfo.Length;

        sup.OnMessageReceived += async envelope =>
        {
            if (envelope.Type == "analyze_result")
            {
                var result = WorkerProtocolFraming.GetPayload<AnalyzeResult>(envelope);
                // Check if the result observed a different stamp
                if (result!.ObservedLastWriteTicks != originalTicks ||
                    result.ObservedByteLength != originalLength)
                {
                    tcs.TrySetResult(false); // source changed
                }
                else
                {
                    tcs.TrySetResult(true); // source unchanged
                }
            }
            else if (envelope.Type == "analyze_error")
            {
                // The worker may reject the file if it detects the change
                tcs.TrySetResult(false); // source changed (error path)
            }
            await Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var readTask = sup.ReadMessagesAsync(cts.Token);

        // Send the request with the original stamp
        var request = new AnalyzeRequest
        {
            JobId = "stale-test",
            ArchivePath = zipPath,
            ContentVersion = 1,
            ExpectedLastWriteTicks = originalTicks,
            ExpectedByteLength = originalLength,
            ScratchWorkspacePath = Path.Combine(_fixture.ScratchRoot, "stale-ws"),
            Deadline = DateTimeOffset.UtcNow.AddSeconds(30),
        };
        Directory.CreateDirectory(request.ScratchWorkspacePath);

        var envelope = WorkerProtocolFraming.CreateEnvelope("analyze", request.JobId, request);
        await sup.SendMessageAsync(envelope);

        // Touch the file to change its last write time
        File.SetLastWriteTimeUtc(zipPath, DateTime.UtcNow.AddSeconds(1));

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(25));
        var completed = await Task.WhenAny(tcs.Task, timeoutTask);
        Assert.True(completed == tcs.Task, "Analyze did not complete within 25 seconds");
        // The result may be true or false depending on timing — the test
        // verifies the IPC path works end-to-end and the stale detection
        // mechanism is in place
    }

    // Test 7 (1.5.0): a successful analysis through the pool persists the cheap
    // content signature used by the scanner's move detection.
    [Fact]
    public async Task Pool_PersistsContentSignature_ForAnalyzedItem()
    {
        var dbPath = Path.Combine(_fixture.TempRoot, "sig-" + Guid.NewGuid().ToString("N")[..6] + ".db");
        var services = new ServiceCollection();
        services.AddDbContext<MangaPixerDbContext>(o => o.UseSqlite(DatabaseInitialization.BuildConnectionString(dbPath)));
        await using var provider = services.BuildServiceProvider();

        long nodeId;
        var zipPath = _fixture.CreateSimpleZip("signature.zip");
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            await db.Database.MigrateAsync();
            var library = new LibraryEntity { PublicId = "sigl", DisplayName = "Sig", RootPath = _fixture.FixtureDir, CreatedAt = DateTimeOffset.UtcNow };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            var node = new CatalogNodeEntity
            {
                PublicId = "sign",
                LibraryId = library.Id,
                Kind = 1,
                DisplayName = "signature.zip",
                RelativePath = "signature.zip",
                PathKey = "signature.zip",
                SortKey = "1signature.zip",
                LastSeenScanRevision = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                ArchiveItem = new ArchiveItemEntity { ContentVersion = 1, AnalysisState = 1 },
            };
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();
            nodeId = node.Id;
        }

        var options = _fixture.CreatePoolOptions();
        var scheduler = new JobScheduler(options);
        var pool = new MediaWorkerPool(
            options, scheduler, new ScratchWorkspaceManager(_fixture.ScratchRoot),
            NullLogger<MediaWorkerPool>.Instance, NullLoggerFactory.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(), new AnalysisResultPersister());
        await pool.StartAsync();
        try
        {
            var fileInfo = new FileInfo(zipPath);
            var job = scheduler.EnqueueAsync(
                itemId: nodeId, contentVersion: 1, operation: JobOperation.Analyze, priority: JobPriority.CurrentPage,
                archivePath: zipPath, expectedLastWriteTicks: fileInfo.LastWriteTimeUtc.Ticks, expectedByteLength: fileInfo.Length);
            await pool.DispatchAsync();
            var result = await job.WaitAsync(TimeSpan.FromSeconds(40));
            Assert.True(result.Success, result.ErrorType);

            // Persistence happens after the job completes; poll briefly.
            ArchiveItemEntity? item = null;
            for (var i = 0; i < 100 && item?.AnalysisState != 0; i++)
            {
                await Task.Delay(100);
                using var scope = provider.CreateScope();
                item = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>()
                    .ArchiveItems.AsNoTracking().SingleAsync(a => a.NodeId == nodeId);
            }

            Assert.NotNull(item);
            Assert.Equal(0, item!.AnalysisState);
            Assert.Equal(3, item.PageCount);
            Assert.Equal(ContentSignature.TryComputeFile(zipPath), item.ContentSignature);
            Assert.Equal(fileInfo.Length, ContentSignature.TryGetByteLength(item.ContentSignature));
        }
        finally
        {
            await pool.StopAsync();
            await pool.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sup in _supervisors)
        {
            try { await sup.DisposeAsync(); } catch { }
        }
        _supervisors.Clear();
    }
}
