namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Creators;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Index;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Tag index registrations (1.45.0). Called once from <c>Program</c>; the facet lanes (F index, Q query) register their services
/// here, never in <c>Program.cs</c>. Lane F: the builder's queue is the app's <see cref="IFacetIndexNudge"/> (registered before the
/// seam's no-op default, which then does not apply), the index service, the creator pass and admin service, and the background builder.
/// </summary>
public static class FacetServicesExtensions
{
    public static IServiceCollection AddFacets(this IServiceCollection services)
    {
        // Lane F: the index builder (queue, creator pass, index service, hosted builder, admin creator API).
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<FacetIndexQueue>();
        services.TryAddSingleton<IFacetIndexNudge>(sp => sp.GetRequiredService<FacetIndexQueue>());
        services.AddSingleton<FacetIndexLock>();
        services.AddSingleton<CreatorStateCache>();
        services.AddScoped<CreatorSync>();
        services.AddScoped<FacetIndexService>();
        services.AddScoped<CreatorAdminService>();
        services.AddHostedService<FacetIndexHostedService>();

        services.TryAddSingleton<IFacetIndexNudge, NoFacetIndexNudge>();
        return services;
    }
}
