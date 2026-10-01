namespace com.lifepixer.mangapixer.Tests.Server.Http;

using com.lifepixer.mangapixer.Server.Media;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// MangaPixer:Media:MaxConcurrentJobs is bound from
/// configuration in Program.cs (rather than hardcoded to the
/// WorkerPoolOptions default of 2, with no override path). Boots the full
/// host via WebApplicationFactory (no HTTP request needed) to verify the
/// value that DI actually resolves.
///
/// Uses the static <see cref="MangaPixerWebApplicationFactory.WithExtraConfiguration"/>
/// factory instead of <c>Environment.SetEnvironmentVariable</c>
/// — a process-global env var here would race with every other
/// concurrently-booting factory (host-booting classes run in parallel, see
/// TestParallelization.cs). The extra-configuration constructor is
/// private (reached only through that static method) so
/// <see cref="MangaPixerWebApplicationFactory"/> still exposes exactly one
/// PUBLIC constructor, which xUnit's <c>IClassFixture&lt;T&gt;</c> requires
/// in other test classes.
/// </summary>
public sealed class WorkerConcurrencyConfigTests
{
    [Fact]
    public async Task MaxConcurrentJobs_BoundFromConfig_OverridesDefault()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(
            new Dictionary<string, string?> { ["MangaPixer:Media:MaxConcurrentJobs"] = "5" });
        var options = factory.Services.GetRequiredService<WorkerPoolOptions>();
        Assert.Equal(5, options.MaxConcurrentJobs);
    }

    [Fact]
    public async Task MaxConcurrentJobs_Unset_KeepsDefaultOfTwo()
    {
        await using var factory = new MangaPixerWebApplicationFactory();
        var options = factory.Services.GetRequiredService<WorkerPoolOptions>();
        Assert.Equal(2, options.MaxConcurrentJobs);
    }

    [Fact]
    public async Task MaxConcurrentJobs_InvalidValue_KeepsDefaultOfTwo()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(
            new Dictionary<string, string?> { ["MangaPixer:Media:MaxConcurrentJobs"] = "not-a-number" });
        var options = factory.Services.GetRequiredService<WorkerPoolOptions>();
        Assert.Equal(2, options.MaxConcurrentJobs);
    }
}
