namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Index;

using com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Creators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Lane F's registrations (1.45.0), called once from <see cref="FacetServicesExtensions.AddFacets"/> BEFORE the seam's no-op nudge default:
/// the builder's queue is the app's <see cref="IFacetIndexNudge"/>, the creator pass, the index service, the admin creator service and the
/// background builder. <see cref="IFacetIndexGeneration"/> is lane Q's registration (optional here).
/// </summary>
public static class FacetIndexServicesExtensions
{
    public static IServiceCollection AddFacetIndex(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<FacetIndexQueue>();
        services.TryAddSingleton<IFacetIndexNudge>(sp => sp.GetRequiredService<FacetIndexQueue>());
        services.AddSingleton<FacetIndexLock>();
        services.AddSingleton<CreatorStateCache>();
        services.AddScoped<CreatorSync>();
        services.AddScoped<FacetIndexService>();
        services.AddScoped<CreatorAdminService>();
        services.AddHostedService<FacetIndexHostedService>();
        return services;
    }
}
