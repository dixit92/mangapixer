namespace com.lifepixer.mangapixer.Server.Media;

/// <summary>
/// Snapshot of one pooled worker as seen by <see cref="WorkerRetirementPolicy"/>.
/// <paramref name="IdleSinceMs"/> is a monotonic <see cref="Environment.TickCount64"/>
/// reading taken when the worker last finished real work (or started).
/// </summary>
internal readonly record struct WorkerIdleState(int Id, bool IsBusy, long IdleSinceMs);

/// <summary>
/// Pure idle-retirement decision for <see cref="MediaWorkerPool"/> (1.22.0).
/// Kept free of locks and processes so the rule is unit-testable; the pool
/// applies the result under its pool lock, which is what makes retirement
/// race-free with dispatch (a slot removed from the pool can no longer be
/// claimed, and a busy slot is never selected).
/// </summary>
internal static class WorkerRetirementPolicy
{
    /// <summary>
    /// Returns the ids of workers to retire now: idle (not busy) workers whose
    /// idle time has reached <paramref name="idleTimeout"/>, longest-idle first,
    /// while never letting the pool fall below <paramref name="minWarmWorkers"/>
    /// (busy workers count toward the warm floor). A non-positive timeout
    /// disables retirement.
    /// </summary>
    public static IReadOnlyList<int> SelectForRetirement(
        IReadOnlyList<WorkerIdleState> workers,
        long nowMs,
        TimeSpan idleTimeout,
        int minWarmWorkers)
    {
        if (idleTimeout <= TimeSpan.Zero || workers.Count == 0)
            return [];

        var retirable = workers.Count - Math.Max(0, minWarmWorkers);
        if (retirable <= 0)
            return [];

        var timeoutMs = (long)idleTimeout.TotalMilliseconds;
        return workers
            .Where(w => !w.IsBusy && nowMs - w.IdleSinceMs >= timeoutMs)
            .OrderBy(w => w.IdleSinceMs)
            .Take(retirable)
            .Select(w => w.Id)
            .ToList();
    }

    /// <summary>
    /// How often the pool re-evaluates retirement: a quarter of the timeout,
    /// bounded to [100 ms, 30 s], so a worker retires within ~25% of its
    /// configured timeout without the sweep itself becoming idle churn.
    /// </summary>
    public static TimeSpan SweepInterval(TimeSpan idleTimeout)
    {
        var quarter = TimeSpan.FromTicks(idleTimeout.Ticks / 4);
        if (quarter < TimeSpan.FromMilliseconds(100))
            return TimeSpan.FromMilliseconds(100);
        return quarter > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : quarter;
    }
}
