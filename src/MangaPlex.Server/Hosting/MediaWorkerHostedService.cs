namespace com.lifepixer.mangaplex.Server.Hosting;

using com.lifepixer.mangaplex.Server.Logging;

using com.lifepixer.mangaplex.Server.Media;
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
        // Light tick: every 2 seconds when idle, dispatch any queued jobs.
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
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
