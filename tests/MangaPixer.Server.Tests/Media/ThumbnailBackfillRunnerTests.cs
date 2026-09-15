namespace com.lifepixer.mangaplex.Tests.Server.Media;

using com.lifepixer.mangaplex.Server.Media;
using Xunit;

/// <summary>
/// Delegate-level unit tests for the continuous thumbnail backfill runner core
/// (post-1.2.0). These exercise the loop logic without a real worker pool, DB,
/// or filesystem — the query/generate/throttle delegates are fakes. Covers:
/// - Processes more than the old 500 cap in one pass
/// - Stops when none remain
/// - Idempotent skip reflected (re-running does nothing)
/// - Attempt-once termination on persistent failure
/// - Throttle/backoff honored when saturated
/// - Cancellation stops the loop
/// </summary>
public sealed class ThumbnailBackfillRunnerTests
{
    private const long LibId = 1L;

    /// <summary>
    /// A fake batch source that returns a fixed queue of batches, removing ids
    /// that the generator marks as "done" (succeeded) so the query naturally
    /// advances — mirroring how successful items leave the real needing set.
    /// </summary>
    private sealed class FakeBatchSource
    {
        private readonly HashSet<long> _remaining;
        private readonly int _batchSize;
        public int QueryCallCount;

        public FakeBatchSource(IEnumerable<long> allIds, int batchSize)
        {
            _remaining = [.. allIds];
            _batchSize = batchSize;
        }

        public Task<List<long>> QueryBatchAsync(long libraryId, int limit, CancellationToken ct)
        {
            QueryCallCount++;
            // Mirror the real query: take up to limit (caller passes batchSize),
            // but the real query also drops items whose thumbnail is now current.
            // The generator signals success by calling MarkDone; we drop those.
            var batch = _remaining.Take(_batchSize).ToList();
            return Task.FromResult(batch);
        }

        public void MarkDone(long id) => _remaining.Remove(id);
        public int Remaining => _remaining.Count;
    }

    [Fact]
    public async Task ProcessesMoreThanOldCap_InOnePass()
    {
        // 600 items — above the old 500 cap.
        var allIds = Enumerable.Range(1, 600).Select(i => (long)i).ToList();
        var source = new FakeBatchSource(allIds, batchSize: 200);
        var generated = new List<long>();

        var attempted = await ThumbnailGenerationService.RunContinuousBackfillCoreAsync(
            libraryId: LibId,
            batchSize: 200,
            backoffMs: 1,
            queryBatchAsync: (lib, limit, ct) => source.QueryBatchAsync(lib, limit, ct),
            generateAsync: (id, ct) => { generated.Add(id); source.MarkDone(id); return Task.FromResult(true); },
            isSaturated: () => false,
            schedulerPendingCount: () => 0,
            ct: CancellationToken.None);

        Assert.Equal(600, attempted);
        Assert.Equal(600, generated.Count);
        // 3 batches of 200 (600 / 200), plus one final empty query to confirm done.
        Assert.True(source.QueryCallCount >= 3);
    }

    [Fact]
    public async Task StopsWhenNoneRemain()
    {
        var source = new FakeBatchSource([], batchSize: 200);

        var attempted = await ThumbnailGenerationService.RunContinuousBackfillCoreAsync(
            libraryId: LibId,
            batchSize: 200,
            backoffMs: 1,
            queryBatchAsync: (lib, limit, ct) => source.QueryBatchAsync(lib, limit, ct),
            generateAsync: (id, ct) => Task.FromResult(true),
            isSaturated: () => false,
            schedulerPendingCount: () => 0,
            ct: CancellationToken.None);

        Assert.Equal(0, attempted);
        Assert.Equal(1, source.QueryCallCount); // one empty query, then stop
    }

    [Fact]
    public async Task Idempotent_RerunningDoesNothing()
    {
        // First pass generates all; second pass finds none remaining.
        var allIds = Enumerable.Range(1, 10).Select(i => (long)i).ToList();
        var source = new FakeBatchSource(allIds, batchSize: 200);

        // First pass
        var first = await ThumbnailGenerationService.RunContinuousBackfillCoreAsync(
            libraryId: LibId, batchSize: 200, backoffMs: 1,
            queryBatchAsync: (lib, limit, ct) => source.QueryBatchAsync(lib, limit, ct),
            generateAsync: (id, ct) => { source.MarkDone(id); return Task.FromResult(true); },
            isSaturated: () => false, schedulerPendingCount: () => 0,
            ct: CancellationToken.None);
        Assert.Equal(10, first);

        // Second pass — nothing remains
        var second = await ThumbnailGenerationService.RunContinuousBackfillCoreAsync(
            libraryId: LibId, batchSize: 200, backoffMs: 1,
            queryBatchAsync: (lib, limit, ct) => source.QueryBatchAsync(lib, limit, ct),
            generateAsync: (id, ct) => { source.MarkDone(id); return Task.FromResult(true); },
            isSaturated: () => false, schedulerPendingCount: () => 0,
            ct: CancellationToken.None);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task PersistentFailure_TerminatesAfterOneAttemptPerItem()
    {
        // Generator always fails — items stay in the needing set (ThumbnailState=2
        // is still "needing"). The loop must attempt each once, then stop when a
        // batch returns only already-attempted items.
        var allIds = Enumerable.Range(1, 5).Select(i => (long)i).ToList();
        var source = new FakeBatchSource(allIds, batchSize: 200);
        var attemptedIds = new List<long>();

        var attempted = await ThumbnailGenerationService.RunContinuousBackfillCoreAsync(
            libraryId: LibId,
            batchSize: 200,
            backoffMs: 1,
            queryBatchAsync: (lib, limit, ct) => source.QueryBatchAsync(lib, limit, ct),
            generateAsync: (id, ct) => { attemptedIds.Add(id); return Task.FromResult(false); }, // always fails
            isSaturated: () => false,
            schedulerPendingCount: () => 0,
            ct: CancellationToken.None);

        // Each of the 5 items attempted exactly once, then the next batch returns
        // the same 5 (still failing) but they're all in the attempted set → stop.
        Assert.Equal(5, attempted);
        Assert.Equal(5, attemptedIds.Count);
        Assert.Equal(allIds, attemptedIds);
    }

    [Fact]
    public async Task Throttle_YieldsWhenSaturated()
    {
        var allIds = Enumerable.Range(1, 3).Select(i => (long)i).ToList();
        var source = new FakeBatchSource(allIds, batchSize: 200);
        var saturationChecks = 0;

        // Saturated for the first 2 checks, then clear.
        bool IsSaturated()
        {
            saturationChecks++;
            return saturationChecks <= 2;
        }

        var attempted = await ThumbnailGenerationService.RunContinuousBackfillCoreAsync(
            libraryId: LibId,
            batchSize: 200,
            backoffMs: 1,
            queryBatchAsync: (lib, limit, ct) => source.QueryBatchAsync(lib, limit, ct),
            generateAsync: (id, ct) => { source.MarkDone(id); return Task.FromResult(true); },
            isSaturated: IsSaturated,
            schedulerPendingCount: () => 0,
            ct: CancellationToken.None);

        // All 3 processed despite throttling; saturation was checked.
        Assert.Equal(3, attempted);
        Assert.True(saturationChecks > 2); // yielded at least twice before proceeding
    }

    [Fact]
    public async Task Throttle_YieldsWhenSchedulerHasPendingWork()
    {
        var allIds = Enumerable.Range(1, 2).Select(i => (long)i).ToList();
        var source = new FakeBatchSource(allIds, batchSize: 200);

        var pendingCountdown = 1; // pending for the first check, then clear

        var attempted = await ThumbnailGenerationService.RunContinuousBackfillCoreAsync(
            libraryId: LibId,
            batchSize: 200,
            backoffMs: 1,
            queryBatchAsync: (lib, limit, ct) => source.QueryBatchAsync(lib, limit, ct),
            generateAsync: (id, ct) => { source.MarkDone(id); return Task.FromResult(true); },
            isSaturated: () => false,
            schedulerPendingCount: () =>
            {
                if (pendingCountdown > 0) { pendingCountdown--; return 1; }
                return 0;
            },
            ct: CancellationToken.None);

        Assert.Equal(2, attempted);
    }

    [Fact]
    public async Task Cancellation_StopsTheLoop()
    {
        var allIds = Enumerable.Range(1, 1000).Select(i => (long)i).ToList();
        var source = new FakeBatchSource(allIds, batchSize: 200);

        using var cts = new CancellationTokenSource();
        var generated = 0;

        // Cancel after the first item is generated.
        var attempted = await ThumbnailGenerationService.RunContinuousBackfillCoreAsync(
            libraryId: LibId,
            batchSize: 200,
            backoffMs: 1,
            queryBatchAsync: (lib, limit, ct) => source.QueryBatchAsync(lib, limit, ct),
            generateAsync: (id, ct) =>
            {
                generated++;
                source.MarkDone(id);
                cts.Cancel(); // stop after first item
                return Task.FromResult(true);
            },
            isSaturated: () => false,
            schedulerPendingCount: () => 0,
            ct: cts.Token);

        // At least one item processed before cancellation; loop did not run to 1000.
        Assert.True(attempted >= 1);
        Assert.True(attempted < 1000);
    }

    [Fact]
    public async Task GenerateException_DoesNotStopLoop()
    {
        var allIds = Enumerable.Range(1, 5).Select(i => (long)i).ToList();
        var source = new FakeBatchSource(allIds, batchSize: 200);
        var generated = new List<long>();

        var attempted = await ThumbnailGenerationService.RunContinuousBackfillCoreAsync(
            libraryId: LibId,
            batchSize: 200,
            backoffMs: 1,
            queryBatchAsync: (lib, limit, ct) => source.QueryBatchAsync(lib, limit, ct),
            generateAsync: (id, ct) =>
            {
                if (id == 3) throw new InvalidOperationException("boom");
                generated.Add(id);
                source.MarkDone(id);
                return Task.FromResult(true);
            },
            isSaturated: () => false,
            schedulerPendingCount: () => 0,
            ct: CancellationToken.None);

        // All 5 attempted (including the one that threw); loop continued.
        Assert.Equal(5, attempted);
        // 4 succeeded (the throwing one stayed in the needing set but was
        // already attempted, so the loop stopped on the next batch).
        Assert.Equal(4, generated.Count);
        Assert.DoesNotContain(3L, generated);
    }
}
