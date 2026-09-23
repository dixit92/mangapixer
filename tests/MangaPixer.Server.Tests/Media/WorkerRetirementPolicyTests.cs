namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Server.Media;
using Xunit;

/// <summary>
/// Idle worker retirement rule (1.22.0), tested without processes: which
/// workers <see cref="WorkerRetirementPolicy"/> selects for a given pool
/// snapshot, clock reading, timeout and warm floor.
/// </summary>
public sealed class WorkerRetirementPolicyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private const long TimeoutMs = 180_000;
    private const long Now = 10_000_000;

    private static WorkerIdleState Idle(int id, long idleForMs) => new(id, IsBusy: false, IdleSinceMs: Now - idleForMs);
    private static WorkerIdleState Busy(int id, long idleForMs = TimeoutMs * 10) => new(id, IsBusy: true, IdleSinceMs: Now - idleForMs);

    [Fact]
    public void IdleWorker_PastTimeout_IsSelected()
    {
        var ids = WorkerRetirementPolicy.SelectForRetirement([Idle(0, TimeoutMs)], Now, Timeout, minWarmWorkers: 0);
        Assert.Equal(new[] { 0 }, ids);
    }

    [Fact]
    public void IdleWorker_BeforeTimeout_IsKept()
    {
        var ids = WorkerRetirementPolicy.SelectForRetirement([Idle(0, TimeoutMs - 1)], Now, Timeout, minWarmWorkers: 0);
        Assert.Empty(ids);
    }

    [Fact]
    public void BusyWorker_IsNeverSelected_HoweverStaleItsIdleClock()
    {
        var ids = WorkerRetirementPolicy.SelectForRetirement([Busy(0), Busy(1)], Now, Timeout, minWarmWorkers: 0);
        Assert.Empty(ids);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTimeout_DisablesRetirement(int timeoutSeconds)
    {
        var ids = WorkerRetirementPolicy.SelectForRetirement(
            [Idle(0, TimeoutMs * 100)], Now, TimeSpan.FromSeconds(timeoutSeconds), minWarmWorkers: 0);
        Assert.Empty(ids);
    }

    [Fact]
    public void WarmFloor_KeepsTheMostRecentlyUsedIdleWorker()
    {
        var ids = WorkerRetirementPolicy.SelectForRetirement(
            [Idle(0, TimeoutMs + 5), Idle(1, TimeoutMs + 50)], Now, Timeout, minWarmWorkers: 1);
        Assert.Equal(new[] { 1 }, ids);
    }

    [Fact]
    public void WarmFloor_CountsBusyWorkers()
    {
        // One busy + one long-idle worker with a floor of 1: the busy worker
        // already satisfies the floor, so the idle one may go.
        var ids = WorkerRetirementPolicy.SelectForRetirement(
            [Busy(0), Idle(1, TimeoutMs)], Now, Timeout, minWarmWorkers: 1);
        Assert.Equal(new[] { 1 }, ids);

        // A floor of 2 keeps both.
        Assert.Empty(WorkerRetirementPolicy.SelectForRetirement(
            [Busy(0), Idle(1, TimeoutMs)], Now, Timeout, minWarmWorkers: 2));
    }

    [Fact]
    public void AllIdleWorkers_PastTimeout_AreSelected_LongestIdleFirst()
    {
        var ids = WorkerRetirementPolicy.SelectForRetirement(
            [Idle(4, TimeoutMs), Idle(7, TimeoutMs * 3), Idle(2, TimeoutMs * 2)], Now, Timeout, minWarmWorkers: 0);
        Assert.Equal(new[] { 7, 2, 4 }, ids);
    }

    [Fact]
    public void EmptyPool_SelectsNothing()
    {
        Assert.Empty(WorkerRetirementPolicy.SelectForRetirement([], Now, Timeout, minWarmWorkers: 0));
    }

    [Theory]
    [InlineData(180_000, 30_000)]   // default 3 min: capped at 30 s
    [InlineData(60_000, 15_000)]    // quarter of the timeout
    [InlineData(200, 100)]          // floor of 100 ms
    public void SweepInterval_IsAQuarterOfTheTimeout_Bounded(int timeoutMs, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs),
            WorkerRetirementPolicy.SweepInterval(TimeSpan.FromMilliseconds(timeoutMs)));
    }
}
