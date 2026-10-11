namespace com.lifepixer.mangapixer.Server.Features.Reading.Suggestions;

/// <summary>DI registration of the 1.41.0 reading-mode suggestions (one call from <c>Program</c>).</summary>
public static class ReadingModeSuggestionServices
{
    public static IServiceCollection AddReadingModeSuggestions(this IServiceCollection services)
    {
        services.AddScoped<ReadingModeSuggestionService>();
        return services;
    }
}
