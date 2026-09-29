namespace com.lifepixer.mangapixer.Server.Hosting;

using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Drains the automatic-matching queue (metadata stage 2), modelled on the scan
/// scheduler: waits <see cref="MetadataAutoMatchOptions.StartupDelay"/> after boot,
/// then runs a pass on every <see cref="MetadataAutoMatchOptions.TickInterval"/>
/// tick or <see cref="MetadataAutoMatchState.Signal"/> (post-scan, bulk, re-run).
/// Single flight (one loop), one work at a time, each in its own DI scope; the
/// gateway paces automatic requests at &lt;= 1/s. When the automatic gate is closed
/// (switch off, backoff, budget spent until the next UTC day) the pass stops and
/// the loop sleeps until the next tick - leased rows are released, and a crash
/// leaves only leases that expire and are picked up again. Failures are logged by
/// type only and never crash the host. 1.29.0: after the matching pass (and the
/// refresh check) each tick also runs one time-sliced tick of the volume-cover pass.
/// </summary>
public sealed class MetadataAutoMatchHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MetadataAutoMatchState _state;
    private readonly MetadataAutoMatchOptions _options;
    private readonly ILogger<MetadataAutoMatchHostedService> _logger;
    private readonly string _owner = "automatch-" + Environment.ProcessId;

    public MetadataAutoMatchHostedService(
        IServiceScopeFactory scopeFactory,
        MetadataAutoMatchState state,
        MetadataAutoMatchOptions options,
        ILogger<MetadataAutoMatchHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WorkerEnabled)
            return;
        try
        {
            if (_options.StartupDelay > TimeSpan.Zero)
                await Task.Delay(_options.StartupDelay, stoppingToken);
            var lastRefresh = DateTimeOffset.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunPassAsync(stoppingToken);
                if (DateTimeOffset.UtcNow - lastRefresh >= _options.RefreshInterval)
                {
                    lastRefresh = DateTimeOffset.UtcNow;
                    await RunRefreshAsync(stoppingToken);
                }
                await RunVolumeCoversAsync(stoppingToken);
                await _state.WaitAsync(_options.TickInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One pass: promote due retries, then process works until the queue is empty or the gate closes.</summary>
    public async Task<int> RunPassAsync(CancellationToken ct)
    {
        var processed = 0;
        try
        {
            using (var scope = _scopeFactory.CreateScope())
                await scope.ServiceProvider.GetRequiredService<MetadataAutoMatchService>().PromoteDueRetriesAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<MetadataAutoMatchService>();
                if (await service.CheckGlobalGateAsync(ct) is { } wait)
                {
                    if (_state.WaitingCode != wait.Code)
                        _logger.LogInformation(LogEvents.Metadata.AutoMatchWaiting, "Automatic matching waiting: {Code}", wait.Code);
                    _state.SetStatus(wait.Code, wait.Until, working: false);
                    break;
                }
                var row = await service.LeaseNextAsync(_owner, ct);
                if (row is null)
                {
                    _state.SetStatus(null, null, working: false);
                    break;
                }
                _state.SetStatus(null, null, working: true);
                try
                {
                    await service.ProcessAsync(row, ct);
                    processed++;
                }
                catch (MetadataGatewayException ex) when (MetadataAutoMatchService.IsRefusal(ex))
                {
                    // Released by ProcessAsync; the gate check above reports why on the next loop.
                    _state.SetStatus(ex.Code, ex.RetryAt, working: false);
                    if (ex.Code is "library_metadata_disabled" or "provider_busy")
                        continue;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _state.SetStatus("error", null, working: false);
            _logger.LogWarning(LogEvents.Metadata.AutoMatchFailed, "Automatic matching pass failed: {Error}", ex.GetType().Name);
        }
        return processed;
    }

    /// <summary>
    /// One tick of the volume-cover / volume-list pass (1.29.0): at most <see cref="Features.Metadata.Volumes.VolumeCoverPass.SliceRequests"/>
    /// requests, so the next matching pass is never far away. Failures are logged by type only.
    /// </summary>
    public async Task RunVolumeCoversAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<Features.Metadata.Volumes.VolumeCoverPass>().RunTickAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Metadata.VolumeCoverPass, "Volume cover pass failed: {Error}", ex.GetType().Name);
        }
    }

    private async Task RunRefreshAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<MetadataRefreshService>().RunPassAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Metadata.RefreshFailed, "Metadata refresh pass failed: {Error}", ex.GetType().Name);
        }
    }
}
