namespace com.lifepixer.mangapixer.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Server.Scanning;

/// <summary>DI registration of cross-library move recognition (1.31.0).</summary>
public static class MovesServiceExtensions
{
    public static IServiceCollection AddLibraryMoves(this IServiceCollection services)
    {
        services.AddScoped<MovePairingService>();
        services.AddScoped<MoveConflictService>();
        services.AddScoped<ITombstoneHolds, MoveTombstoneHolds>();
        services.AddSingleton<MovePairingRunner>();
        services.AddSingleton<IMovePairingTrigger>(sp => sp.GetRequiredService<MovePairingRunner>());
        services.AddHostedService<MovePairingStartupHostedService>();
        return services;
    }
}
