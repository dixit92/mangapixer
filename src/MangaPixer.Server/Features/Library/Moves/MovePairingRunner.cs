namespace com.lifepixer.mangapixer.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Server.Logging;
using Microsoft.Extensions.Hosting;

/// <summary>Asks for a pass of move recognition after the fact (1.31.0): after a scan, after an analysis stored a signature.</summary>
public interface IMovePairingTrigger
{
    /// <summary>Starts a pass soon, or one more after the running one. Never blocks; never throws.</summary>
    Task RequestRun();
}

/// <summary>
/// Single-flight runner of <see cref="MovePairingService"/> (1.31.0). Requests coalesce: a pass waits <see cref="Debounce"/>
/// first (an analysis burst of thousands of archives costs a handful of passes), and requests made while a pass runs give one
/// more pass. Also runs once at startup (<see cref="MovePairingStartupHostedService"/>) - which is what repairs moves made
/// before the upgrade. Each pass uses its own DI scope; failures are logged by type only.
/// </summary>
public sealed class MovePairingRunner : IMovePairingTrigger
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MovePairingRunner> _logger;
    private readonly CancellationToken _stopping;
    private readonly object _gate = new();
    private Task? _running;
    private bool _rerunRequested;

    public MovePairingRunner(IServiceScopeFactory scopeFactory, ILogger<MovePairingRunner> logger, IHostApplicationLifetime? lifetime = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    /// <summary>Wait before a pass so that requests arriving together share it.</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(5);

    public Task RequestRun()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                _rerunRequested = true;
                return _running;
            }
            _rerunRequested = false;
            _running = Task.Run(() => RunLoopAsync(_stopping), CancellationToken.None);
            return _running;
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Debounce > TimeSpan.Zero)
                    await Task.Delay(Debounce, ct);
                lock (_gate)
                    _rerunRequested = false;
                using (var scope = _scopeFactory.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<MovePairingService>().RunAsync(ct);
                lock (_gate)
                {
                    if (!_rerunRequested)
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Scanning.MovePairingFailed, "Move pairing pass failed: {Error}", ex.GetType().Name);
        }
    }
}

/// <summary>Startup pass of move recognition after the fact (1.31.0); fire-and-forget, never blocks startup.</summary>
public sealed class MovePairingStartupHostedService : IHostedService
{
    private readonly MovePairingRunner _runner;

    public MovePairingStartupHostedService(MovePairingRunner runner) => _runner = runner;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = _runner.RequestRun();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
