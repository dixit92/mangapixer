namespace com.lifepixer.mangaplex.Server.Hosting;

using com.lifepixer.mangaplex.Server.Logging;

using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Periodic maintenance: hourly expired-session cleanup and daily cache
/// eviction pass. Uses a single timer with bounded drift; failures are
/// logged as warnings and do not crash the host.
/// </summary>
public sealed class MaintenanceHostedService : IHostedService, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MaintenanceHostedService> _logger;
    private Timer? _sessionTimer;
    private Timer? _cacheTimer;
    private readonly TimeSpan _sessionInterval = TimeSpan.FromHours(1);
    private readonly TimeSpan _cacheInterval = TimeSpan.FromHours(24);

    public MaintenanceHostedService(
        IServiceProvider services,
        ILogger<MaintenanceHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionTimer = new Timer(_ => RunSafe(CleanupSessionsAsync, "session cleanup"), null,
            TimeSpan.FromMinutes(1), _sessionInterval);
        _cacheTimer = new Timer(_ => RunSafe(RunCacheEvictionAsync, "cache eviction"), null,
            TimeSpan.FromMinutes(5), _cacheInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _cacheTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        await Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _sessionTimer?.Dispose();
        _cacheTimer?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void RunSafe(Func<Task> work, string label)
    {
        // Timer callbacks must never throw unhandled into the host.
        _ = Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.Backup.MaintenanceFailed, "Maintenance {Label} failed: {Error}", label, ex.GetType().Name);
            }
        });
    }

    private async Task CleanupSessionsAsync()
    {
        using var scope = _services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
        await sessions.CleanupExpiredSessionsAsync();
    }

    private async Task RunCacheEvictionAsync()
    {
        using var scope = _services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<CacheService>();
        // Routine over-budget eviction — does not log a disk-full warning
        // (audit defect D27). HandleDiskFull is reserved for real IOException
        // disk-full paths.
        cache.EvictOverBudget();
        await Task.CompletedTask;
    }
}
