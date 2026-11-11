namespace com.lifepixer.mangapixer.Server.Features.Recommendations;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>Recommendations registrations (1.53.0 lane C), called once from <c>Program</c>: the scope index cache and the service.</summary>
public static class RecommendationServicesExtensions
{
    public static IServiceCollection AddRecommendations(this IServiceCollection services)
    {
        services.TryAddSingleton<RecommendationIndexCache>();
        services.TryAddScoped<RecommendationService>();
        return services;
    }
}
