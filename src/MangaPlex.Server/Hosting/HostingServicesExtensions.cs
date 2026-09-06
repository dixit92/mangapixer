namespace com.lifepixer.mangaplex.Server.Hosting;

using com.lifepixer.mangaplex.Server.Media;
using com.lifepixer.mangaplex.Server.Operations;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Features.Reading;
using com.lifepixer.mangaplex.Server.Scanning;
using com.lifepixer.mangaplex.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        services.AddSingleton<AppRootOptions>();
        services.AddScoped<IdentityRelinkService>();

        // Page delivery depends on DbContext + CacheService + JobScheduler,
        // all of which are registered by AddMangaPlexAuth/AddMangaPlexMedia.
        services.AddScoped<PageDeliveryService>();

        // WriteCoordinator for serialized DB writes.
        services.AddScoped<WriteCoordinator>();

        // Hosted services — order matters for startup recovery, which runs
        // before the worker pool starts dispatching.
        services.AddHostedService<StartupRecoveryHostedService>();
        services.AddHostedService<MediaWorkerHostedService>();
        services.AddHostedService<MaintenanceHostedService>();

        return services;
    }
}
