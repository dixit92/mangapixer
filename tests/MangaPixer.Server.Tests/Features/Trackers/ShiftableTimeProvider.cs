namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

/// <summary>
/// The system clock plus an offset a test can move forward (1.44.0, flaky rate-limit retry test). Until <see cref="Advance"/> is called it
/// IS the system clock: real time, real timers - so everything else in the host (pacing, token renewal) keeps its normal timing. Advance
/// moves "now" and fires every one-shot timer whose due time it passes (e.g. the relay's <c>Task.Delay(delay, time)</c> before a
/// rate-limited retry), so a test can use a long tracker delay - no race with its own assertions on a loaded box - and still finish at once.
/// A periodic timer keeps its real schedule.
/// </summary>
internal sealed class ShiftableTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ShiftTimer> _timers = [];
    private TimeSpan _offset;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return TimeProvider.System.GetUtcNow() + _offset;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ShiftTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward and fires the one-shot timers now due.</summary>
    public void Advance(TimeSpan by)
    {
        List<ShiftTimer> due;
        lock (_gate)
        {
            _offset += by;
            var now = TimeProvider.System.GetUtcNow() + _offset;
            due = _timers.Where(t => t.DueAt is { } at && at <= now).ToList();
        }
        foreach (var timer in due)
            timer.Fire();
    }

    /// <summary>
    /// Waits until a one-shot timer whose original delay matches <paramref name="dueTime"/> is pending - the code under test may create its
    /// timer on another thread after the call that scheduled it returned; advancing before that would move the clock under it.
    /// </summary>
    public async Task<bool> WaitForPendingAsync(Func<TimeSpan, bool> dueTime, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_timers.Any(t => t.DueAt is not null && dueTime(t.DueTime)))
                    return true;
            }
            await Task.Delay(20);
        }
        return false;
    }

    private void Track(ShiftTimer timer, bool tracked)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
            if (tracked)
                _timers.Add(timer);
        }
    }

    private sealed class ShiftTimer(ShiftableTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private ITimer? _inner;
        private int _fired;

        /// <summary>When a one-shot timer is due on the shifted clock; null for a periodic or stopped timer.</summary>
        public DateTimeOffset? DueAt { get; private set; }

        /// <summary>The delay it was created (or last changed) with.</summary>
        public TimeSpan DueTime { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _inner?.Dispose();
            Interlocked.Exchange(ref _fired, 0);
            var oneShot = dueTime != Timeout.InfiniteTimeSpan && period == Timeout.InfiniteTimeSpan;
            DueAt = oneShot ? owner.GetUtcNow() + dueTime : null;
            DueTime = dueTime;
            owner.Track(this, oneShot);
            _inner = TimeProvider.System.CreateTimer(_ => Fire(), null, dueTime, period);
            return true;
        }

        /// <summary>Runs the callback once for a one-shot timer (whichever comes first: the real timer or <see cref="Advance"/>).</summary>
        public void Fire()
        {
            if (DueAt is not null && Interlocked.Exchange(ref _fired, 1) == 1)
                return;
            if (DueAt is not null)
                owner.Track(this, false);
            callback(state);
        }

        public void Dispose()
        {
            _inner?.Dispose();
            owner.Track(this, false);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
