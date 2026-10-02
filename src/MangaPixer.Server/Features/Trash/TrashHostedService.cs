namespace com.lifepixer.mangapixer.Server.Features.Trash;

using com.lifepixer.mangapixer.Core.Scheduling;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

/// <summary>
/// When the daily automatic trash run is due (1.31.0). Pure. The run is at the admin's hour (default <see cref="RunHour"/>:00)
/// server local time - by default a quiet hour for readers; a library that is being scanned at that moment is skipped until the next day. A run missed while
/// the server was down is caught up once at start-up, but never one from before automatic cleaning was turned on (turning it
/// on is the admin's approval).
/// </summary>
public static class TrashSchedule
{
    /// <summary>The default local hour of the automatic run (the admin can choose another, <see cref="HourOf"/>).</summary>
    public const int RunHour = 4;

    /// <summary>The effective hour: the stored one when it is 0-23, else <see cref="RunHour"/>.</summary>
    public static int HourOf(int? stored) => stored is >= 0 and <= 23 ? stored.Value : RunHour;

    public static bool IsValidHour(int hour) => hour is >= 0 and <= 23;

    /// <summary>
    /// The next due time: now when the latest daily slot passed after both the last run and the moment automatic cleaning was
    /// turned on (a missed run); otherwise the next slot after now.
    /// </summary>
    public static DateTimeOffset NextDue(DateTimeOffset nowUtc, TimeZoneInfo zone, DateTimeOffset? lastRunUtc, DateTimeOffset? enabledAtUtc) =>
        NextDue(nowUtc, zone, lastRunUtc, enabledAtUtc, RunHour);

    /// <summary>
    /// <see cref="NextDue(DateTimeOffset, TimeZoneInfo, DateTimeOffset?, DateTimeOffset?)"/> at the admin's <paramref name="hour"/>.
    /// Changing the hour never triggers a run for a slot the previous run or the switch-on already covers.
    /// </summary>
    public static DateTimeOffset NextDue(DateTimeOffset nowUtc, TimeZoneInfo zone, DateTimeOffset? lastRunUtc, DateTimeOffset? enabledAtUtc, int hour)
    {
        // 1.32.0: the shared rule of the scheduled jobs; the floor is the later of the last run and the switch-on.
        DateTimeOffset? floor = lastRunUtc is { } a && (enabledAtUtc is not { } b || a > b) ? a : enabledAtUtc;
        return JobSchedule.NextDue(nowUtc, zone, new TimeOfDaySchedule(hour), floor);
    }
}

/// <summary>
/// The daily automatic "Empty trash" + "Clean bundles" (1.31.0), only while the admin's "Turn automatic cleaning on" is on.
/// A loop on the registered <see cref="TimeProvider"/>: it re-reads the setting at least every <see cref="CheckInterval"/>, so
/// turning it on or off applies without a restart. Failures are logged (type only) and never crash the host.
/// </summary>
public sealed class TrashHostedService : BackgroundService
{
    /// <summary>Let start-up recovery and the first scans settle before a caught-up run.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<TrashHostedService> _logger;

    public TrashHostedService(IServiceScopeFactory scopes, TimeProvider time, ILogger<TrashHostedService> logger)
    {
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var earliest = _time.GetUtcNow() + InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = CheckInterval;
            try
            {
                var now = _time.GetUtcNow();
                var settings = await ReadSettingsAsync(stoppingToken);
                if (settings is { TrashAutoCleanEnabled: true })
                {
                    var due = TrashSchedule.NextDue(now, _time.LocalTimeZone, settings.TrashLastAutoRunAt, settings.TrashAutoCleanEnabledAt,
                        TrashSchedule.HourOf(settings.TrashAutomaticHour));
                    if (due < earliest)
                        due = earliest;
                    if (due <= now)
                    {
                        await RunPassAsync(stoppingToken);
                        continue;
                    }
                    if (due - now < wait)
                        wait = due - now;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.Administration.TrashRunFailed, "Automatic trash run failed: {Error}", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(wait, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One automatic pass: Empty trash for every library without a hold, then Clean bundles. Records the attempt first, so a
    /// failing pass is not retried until the next day. Does nothing when automatic cleaning is off.
    /// </summary>
    public async Task<bool> RunPassAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId, ct);
        if (row is not { TrashAutoCleanEnabled: true })
            return false;
        row.TrashLastAutoRunAt = _time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        var trash = scope.ServiceProvider.GetRequiredService<TrashService>();
        await trash.EmptyAsync(null, releaseHold: false, automatic: true, actor: null, ct);
        await trash.CleanBundlesAsync(automatic: true, actor: null, ct);
        return true;
    }

    private async Task<AppSettingsEntity?> ReadSettingsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId, ct);
    }
}
