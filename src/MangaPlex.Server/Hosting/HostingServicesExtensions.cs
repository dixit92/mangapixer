namespace com.lifepixer.mangaplex.Server.Hosting;

using com.lifepixer.mangaplex.Server.Media;
using com.lifepixer.mangaplex.Server.Operations;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Features.Reading;
using com.lifepixer.mangaplex.Server.Scanning;
using com.lifepixer.mangaplex.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;

/// <summary>
/// DI registration extensions for hosted lifecycle services and the
/// supporting services they depend on.
/// </summary>
public static class HostingServicesExtensions
{
    /// <summary>
    /// Registers hosted services (worker pool, startup recovery, maintenance)
    /// and the catalog/storage/operations services they require that are not
    /// already registered by <c>AddMangaPlexAuth</c> or <c>AddMangaPlexMedia</c>.
    /// </summary>
    public static IServiceCollection AddMangaPlexHosting(this IServiceCollection services)
    {
        // Storage / scanning services that were implemented but never registered.
        services.AddScoped<LibraryRegistrationService>();
        services.AddScoped<ScanLeaseService>();
        services.AddScoped<LibraryMaintenanceService>();
        services.AddSingleton<LibraryScanPolicy>();
        services.AddSingleton<ScanRunRegistry>();
        services.AddSingleton<AppRootOptions>(sp =>
        {
            var config = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            return new AppRootOptions
            {
                DataRoot = config["MangaPlex:Storage:DataRoot"],
                CacheRoot = config["MangaPlex:Storage:CacheRoot"],
                ScratchRoot = config["MangaPlex:Storage:ScratchRoot"],
            };
        });
        services.AddScoped<IdentityRelinkService>();

        // Admin directory browser for the library-registration path picker.
        // Confined to the configured media browse root (default /media).
        services.AddSingleton<MediaBrowseOptions>(sp =>
        {
            var config = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            var configured = config["MangaPlex:Storage:MediaRoot"];
            return new MediaBrowseOptions
            {
                Root = string.IsNullOrWhiteSpace(configured) ? "/media" : configured,
            };
        });
        services.AddScoped<FilesystemBrowseService>();

        // Page delivery depends on DbContext + CacheService + JobScheduler,
        // all of which are registered by AddMangaPlexAuth/AddMangaPlexMedia.
        services.AddScoped<PageDeliveryService>();

        // WriteCoordinator for serialized DB writes.
        services.AddScoped<WriteCoordinator>();

        // JobRecoveryService — used by StartupRecoveryHostedService to recover
        // interrupted jobs, analyses, and scratch workspaces. Was missing in
        // the I01 wiring, which caused "No service for type JobRecoveryService"
        // at startup (audit defect D15/D25).
        services.AddScoped<JobRecoveryService>();

        // Rotating DB backups — scheduled online snapshots (VACUUM INTO) with
        // retention-based pruning. Interval/retention are admin-configurable
        // via MangaPlex:Backups:*; backups land in <dataRoot>/backups, the
        // same folder as pre-migration backups (which are never pruned).
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            var dataRoot = config["MangaPlex:Storage:DataRoot"];
            var backupsDir = Path.Combine(
                string.IsNullOrWhiteSpace(dataRoot)
                    ? Path.Combine(AppContext.BaseDirectory, "data")
                    : dataRoot,
                "backups");

            var options = new RotatingBackupOptions { BackupDirectory = backupsDir };
            if (double.TryParse(config["MangaPlex:Backups:IntervalHours"],
                    System.Globalization.CultureInfo.InvariantCulture, out var hours) && hours > 0)
                options.Interval = TimeSpan.FromHours(hours);
            if (int.TryParse(config["MangaPlex:Backups:RetentionCount"], out var retention) && retention > 0)
                options.RetentionCount = retention;
            if (bool.TryParse(config["MangaPlex:Backups:Enabled"], out var enabled))
                options.Enabled = enabled;
            return options;
        });
        services.AddSingleton<RotatingBackupState>();
        services.AddScoped<RotatingBackupService>();

        // Hosted services — order matters for startup recovery, which runs
        // before the worker pool starts dispatching.
        services.AddHostedService<StartupRecoveryHostedService>();
        services.AddHostedService<MediaWorkerHostedService>();
        services.AddHostedService<MaintenanceHostedService>();
        services.AddHostedService<RotatingBackupHostedService>();

        return services;
    }
}
