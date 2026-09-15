namespace com.lifepixer.mangapixer.Server.Hosting;

using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Runs one-time recovery work on startup before the API accepts traffic:
/// schema validation, interrupted job/analysis recovery, scratch workspace
/// cleanup, and expired scan-lease recovery. Failures are logged as warnings
/// and do not block API startup — health checks remain authoritative for
/// readiness probes.
/// </summary>
public sealed class StartupRecoveryHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<StartupRecoveryHostedService> _logger;

    public StartupRecoveryHostedService(
        IServiceProvider services,
        ILogger<StartupRecoveryHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;

        try
        {
            var recovery = sp.GetRequiredService<JobRecoveryService>();

            var schema = await recovery.ValidateSchemaAsync(cancellationToken);
            if (!schema.Valid)
            {
                _logger.LogWarning(LogEvents.Database.SchemaValidationFailedRecoverySkipped, "Schema validation failed: {Error}. Recovery skipped.", schema.Error);
                return;
            }

            var jobs = await recovery.RecoverInterruptedJobsAsync(cancellationToken);
            var analysis = await recovery.RecoverInterruptedAnalysisAsync(cancellationToken);
            var scratch = recovery.RecoverScratchWorkspaces(TimeSpan.FromMinutes(15));

            var leaseService = sp.GetRequiredService<ScanLeaseService>();
            var leases = await leaseService.RecoverExpiredLeasesAsync(cancellationToken);

            _logger.LogInformation(
                LogEvents.Database.StartupRecoveryCompleted, "Startup recovery complete: {Jobs} interrupted jobs, {Analysis} interrupted analyses, {Scratch} stale scratch workspaces, {Leases} expired scan leases",
                jobs, analysis, scratch, leases);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Database.StartupRecoveryFailed, "Startup recovery failed: {Error}. API remains available.", ex.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
