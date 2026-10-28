namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// The tag query's registrations (1.45.0, lane Q), called from <see cref="FacetServicesExtensions.AddFacets"/>: the index generation
/// seam (lane F's writer bumps it), the count cache, the viewer scope, the counts, the Info panel and the tag-view search.
/// </summary>
public static class QueryServicesExtensions
{
    public static IServiceCollection AddFacetQuery(this IServiceCollection services)
    {
        services.TryAddSingleton<IFacetIndexGeneration, FacetIndexGeneration>();
        services.TryAddSingleton<FacetCountCache>();
        services.TryAddScoped<FacetViewerScopeResolver>();
        services.TryAddScoped<FacetCountService>();
        services.TryAddScoped<InfoPanelService>();
        services.TryAddScoped<FacetSearchService>();
        services.TryAddScoped<FacetSuggestService>(); // 1.49.0 lane O: creator / tag values for the quick results
        services.TryAddScoped<Catalog.SearchSuggestService>(); // 1.49.0 lane O: the quick-results route's service
        return services;
    }
}
