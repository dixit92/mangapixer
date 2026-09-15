namespace com.lifepixer.mangapixer.Server.Hosting;

using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Server.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted service that starts and stops the <see cref="MediaWorkerPool"/>.
/// Worker startup failure is non-fatal: the API stays up and a sanitized
/// warning is logged. Readers will see "preparing" states until the pool
/// recovers or the admin investigates.
/// </summary>
public sealed class MediaWorkerHostedService : IHostedService, IAsyncDisposable
{
    /// <summary>
    /// Fallback poll interval when idle. Dispatch itself is event-driven (a
    /// freed worker slot wakes the loop immediately via
    /// <see cref="MediaWorkerPool.WaitForDispatchSignalAsync"/>); this bound
    /// only covers jobs enqueued directly via the JobScheduler without a
    /// manual dispatch nudge (background analysis resume, admin re-analyze).
    /// Replaces the old fixed 2s tick, which made every idle cycle ~83% dead
    /// time and capped single-worker throughput at ~25/min regardless of
    /// per-job cost (~0.4s warm).
    /// </summary>
    private static readonly TimeSpan DispatchFallbackPoll = TimeSpan.FromMilliseconds(200);

    private readonly MediaWorkerPool _pool;
    private readonly ILogger<MediaWorkerHostedService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private Task? _dispatchLoop;
    private CancellationTokenSource? _cts;

    public MediaWorkerHostedService(
        MediaWorkerPool pool,
        ILogger<MediaWorkerHostedService> logger,
        IHostApplicationLifetime lifetime)
    {
        _pool = pool;
        _logger = logger;
        _lifetime = lifetime;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _pool.StartAsync(cancellationToken);
            _logger.LogInformation(LogEvents.Worker.HostedPoolStarted, "Media worker pool started with {Count} worker(s)", _pool.WorkerCount);

            // Start a low-frequency dispatch loop so background analysis jobs
            // are pulled when reader demand is idle. Reader-demand dispatch
            // is invoked synchronously by the page delivery path.
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            _dispatchLoop = Task.Run(() => DispatchLoopAsync(_cts.Token), _cts.Token);
        }
        catch (Exception ex)
        {
            // Non-fatal: API stays up. Readers will see pending/preparing states.
            _logger.LogWarning(LogEvents.Worker.PoolStartFailed, "Media worker pool failed to start: {Error}. API remains available.", ex.GetType().Name);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cts?.Cancel();
            if (_dispatchLoop is not null)
            {
                try { await _dispatchLoop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
                catch { /* best effort */ }
            }
            await _pool.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Worker.PoolStopError, "Error stopping media worker pool: {Error}", ex.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        await _pool.DisposeAsync();
    }

    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        // Event-driven: DispatchAsync fills every available/reservation-eligible
        // slot in one pass, then this loop waits for a slot to free (signaled
        // immediately) or the short fallback poll, whichever comes first.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _pool.DispatchAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.Worker.DispatchLoopError, "Worker dispatch loop error: {Error}", ex.GetType().Name);
            }

            try
            {
                await _pool.WaitForDispatchSignalAsync(DispatchFallbackPoll, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
