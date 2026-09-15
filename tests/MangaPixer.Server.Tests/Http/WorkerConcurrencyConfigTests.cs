namespace com.lifepixer.mangaplex.Tests.Server.Http;

using com.lifepixer.mangaplex.Server.Media;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// MangaPlex:Media:MaxConcurrentJobs is bound from
/// configuration in Program.cs (rather than hardcoded to the
/// WorkerPoolOptions default of 2, with no override path). Boots the full
/// host via WebApplicationFactory (no HTTP request needed) to verify the
/// value that DI actually resolves.
///
/// Uses the static <see cref="MangaPlexWebApplicationFactory.WithExtraConfiguration"/>
/// factory instead of <c>Environment.SetEnvironmentVariable</c>
/// — a process-global env var here would race with every other
/// concurrently-booting factory once assembly parallelization is restored
/// (see TestParallelization.cs). The extra-configuration constructor is
/// private (reached only through that static method) so
/// <see cref="MangaPlexWebApplicationFactory"/> still exposes exactly one
/// PUBLIC constructor, which xUnit's <c>IClassFixture&lt;T&gt;</c> requires
/// in other test classes.
///
/// In the "HttpSerial" collection alongside every other
/// WebApplicationFactory-booting Server.Tests class — not for storage
/// isolation, but because every host boot unconditionally reassigns the
/// process-global Serilog <c>Log.Logger</c> static in <c>Program.Main</c>
/// (see the remarks on HostingCorrectnessTests).
/// </summary>
[Collection("HttpSerial")]
public sealed class WorkerConcurrencyConfigTests
{
    [Fact]
    public async Task MaxConcurrentJobs_BoundFromConfig_OverridesDefault()
    {
        await using var factory = MangaPlexWebApplicationFactory.WithExtraConfiguration(
            new Dictionary<string, string?> { ["MangaPlex:Media:MaxConcurrentJobs"] = "5" });
        var options = factory.Services.GetRequiredService<WorkerPoolOptions>();
        Assert.Equal(5, options.MaxConcurrentJobs);
    }

    [Fact]
    public async Task MaxConcurrentJobs_Unset_KeepsDefaultOfTwo()
    {
        await using var factory = new MangaPlexWebApplicationFactory();
        var options = factory.Services.GetRequiredService<WorkerPoolOptions>();
        Assert.Equal(2, options.MaxConcurrentJobs);
    }

    [Fact]
    public async Task MaxConcurrentJobs_InvalidValue_KeepsDefaultOfTwo()
    {
        await using var factory = MangaPlexWebApplicationFactory.WithExtraConfiguration(
            new Dictionary<string, string?> { ["MangaPlex:Media:MaxConcurrentJobs"] = "not-a-number" });
        var options = factory.Services.GetRequiredService<WorkerPoolOptions>();
        Assert.Equal(2, options.MaxConcurrentJobs);
    }
}
