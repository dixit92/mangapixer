namespace com.lifepixer.mangaplex.Server;

/// <summary>
/// Minimal Program.cs for P00: health-only startup with no library configuration.
/// Full API endpoints are added in later packages.
/// </summary>
public sealed partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Health checks only — no database, no auth, no library registration in P00.
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("MangaPlex server is running"));

        var app = builder.Build();

        // Version prefix reserved for future API endpoints.
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/health/ready");

        app.Run();
    }
}
