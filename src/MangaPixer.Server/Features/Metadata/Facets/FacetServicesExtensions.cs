namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Index;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Tag index registrations (1.45.0). Called once from <c>Program</c>; the facet lanes (F index, Q query) register their services
/// here, never in <c>Program.cs</c>. The seam's <see cref="NoFacetIndexNudge"/> is the default <see cref="IFacetIndexNudge"/>; lane F
/// replaces it with its builder's queue (register it BEFORE the default line, or replace the descriptor).
/// </summary>
public static class FacetServicesExtensions
{
    public static IServiceCollection AddFacets(this IServiceCollection services)
    {
        services.AddFacetIndex(); // Lane F: the index builder; its queue is the app's nudge (before the default below).
        services.TryAddSingleton<IFacetIndexNudge, NoFacetIndexNudge>();
        services.AddFacetQuery(); // lane Q: counts, the Info panel, the tag view
        return services;
    }
}
