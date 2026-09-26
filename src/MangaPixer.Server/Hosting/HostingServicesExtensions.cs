namespace com.lifepixer.mangapixer.Server.Hosting;

using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Features.Import.YacReader;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// already registered by <c>AddMangaPixerAuth</c> or <c>AddMangaPixerMedia</c>.
    /// </summary>
    public static IServiceCollection AddMangaPixerHosting(this IServiceCollection services)
    {
        // Storage / scanning services that were implemented but never registered.
        services.AddScoped<LibraryRegistrationService>();
        services.AddScoped<ScanLeaseService>();
        services.AddScoped<LibraryMaintenanceService>();
        services.AddSingleton<LibraryScanPolicy>();
        services.AddSingleton<ScanRunRegistry>();
        // Scan launch path shared by the admin scan endpoints and the scan
        // scheduler (1.23.0); the scheduler evaluates per-library schedules.
        services.AddScoped<LibraryScanLauncher>();
        services.AddSingleton(sp => LibraryScanSchedulerOptions.FromConfiguration(
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
        services.AddSingleton<LibraryScanScheduler>();
        services.AddSingleton<AppRootOptions>(sp =>
        {
            var config = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            return new AppRootOptions
            {
                DataRoot = config["MangaPixer:Storage:DataRoot"],
                CacheRoot = config["MangaPixer:Storage:CacheRoot"],
                ScratchRoot = config["MangaPixer:Storage:ScratchRoot"],
            };
        });
        services.AddScoped<IdentityRelinkService>();

        // YACReader progress importer (admin-only). The library reader is a
        // stateless singleton; the import service is scoped (depends on DbContext).
        services.AddSingleton<YacReaderLibraryReader>();
        services.AddScoped<YacReaderImportService>();

        // Admin directory browser for the library-registration path picker.
        // Confined to the configured media browse root (default /media).
        services.AddSingleton<MediaBrowseOptions>(sp =>
        {
            var config = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            var configured = config["MangaPixer:Storage:MediaRoot"];
            return new MediaBrowseOptions
            {
                Root = string.IsNullOrWhiteSpace(configured) ? "/media" : configured,
            };
        });
        services.AddScoped<FilesystemBrowseService>();

        // Page delivery depends on DbContext + CacheService + JobScheduler,
        // all of which are registered by AddMangaPixerAuth/AddMangaPixerMedia.
        services.AddScoped<PageDeliveryService>();

        // WriteCoordinator for serialized DB writes.
        services.AddScoped<WriteCoordinator>();

        // JobRecoveryService — used by StartupRecoveryHostedService to recover
        // interrupted jobs, analyses, and scratch workspaces. Was missing in
        // the I01 wiring, which caused "No service for type JobRecoveryService"
        // at startup (audit defect D15/D25).
        services.AddScoped<JobRecoveryService>();

        // Rotating DB backups — scheduled online snapshots (VACUUM INTO) with
        // retention-based pruning. Backup settings (1.22.0) resolve per field
        // from configuration (MangaPixer:Backups:*), then the admin UI's
        // AppSettings row, then the defaults; the rotating folder is
        // <dataRoot>/backups or a validated custom location. Pre-migration and
        // pre-restore safety snapshots always stay in <dataRoot>/backups.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            return RotatingBackupOptions.FromConfiguration(
                config, BackupSettingsResolver.SafetyDirectoryFor(config["MangaPixer:Storage:DataRoot"]));
        });
        services.AddSingleton<BackupSettingsResolver>();
        services.AddSingleton<IBackupLocationFileSystem, PhysicalBackupLocationFileSystem>();
        services.AddSingleton(sp => new BackupLocationValidator(
            sp.GetRequiredService<IBackupLocationFileSystem>(),
            OperatingSystem.IsWindows() ? BackupPathFlavor.Windows : BackupPathFlavor.Unix));
        services.AddSingleton<RotatingBackupState>();
        services.AddSingleton<BackupSnapshotMoveService>();
        services.AddScoped<BackupLocationService>();
        services.AddScoped<BackupSettingsService>();
        services.AddScoped<RotatingBackupService>();

        // DB backup import/restore (1.7.0). Admin-only upload + validate +
        // stage; the atomic swap is applied on the next restart (see
        // Program.cs). Size cap is admin-configurable via
        // MangaPixer:Backups:MaxRestoreUploadBytes (default 512 MiB).
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            var options = new DbRestoreOptions();
            if (long.TryParse(config["MangaPixer:Backups:MaxRestoreUploadBytes"],
                    System.Globalization.CultureInfo.InvariantCulture, out var cap) && cap > 0)
                options.MaxUploadBytes = cap;
            return options;
        });
        services.AddScoped<DbRestoreService>();

        // Administrative audit trail (1.18.0): centralised write (used by the
        // admin + operations controllers) and the paged, admin-only read path
        // that backs the audit-trail UI. The audit_events store predates this;
        // this only adds read/write access on top.
        services.AddScoped<com.lifepixer.mangapixer.Server.Features.Admin.AuditService>();

        // Admin analytics read surface (1.22.0 lane E): on-demand aggregation
        // over library/content/engagement counts, reusing DiagnosticsService
        // (registered separately via AddScoped<DiagnosticsService>() in
        // Program.cs) where it overlaps.
        services.AddScoped<com.lifepixer.mangapixer.Server.Features.Analytics.AnalyticsService>();

        // Hosted services — order matters for startup recovery, which runs
        // before the worker pool starts dispatching. The thumbnail backfill
        // runs after the worker pool so it can dispatch generation jobs.
        services.AddHostedService<StartupRecoveryHostedService>();
        services.AddHostedService<MediaWorkerHostedService>();
        services.AddHostedService<PendingAnalysisResumeHostedService>();
        services.AddHostedService<ThumbnailBackfillHostedService>();
        // ComicInfo.xml backfill (1.24.0): single-flight singleton pass, kicked at
        // startup (after the worker pool) and after every successful scan.
        services.AddSingleton<com.lifepixer.mangapixer.Server.Features.Metadata.ComicInfoBackfillService>();
        services.AddHostedService<ComicInfoBackfillHostedService>();
        services.AddHostedService<MaintenanceHostedService>();
        services.AddHostedService<RotatingBackupHostedService>();
        // Automatic per-library scans (1.23.0): after startup recovery, with its
        // own startup grace period so boot does not trigger a scan burst.
        services.AddHostedService<LibraryScanSchedulerHostedService>();

        return services;
    }
}
