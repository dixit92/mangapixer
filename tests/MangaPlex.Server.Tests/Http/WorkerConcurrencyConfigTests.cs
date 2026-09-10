namespace com.lifepixer.mangaplex.Tests.Server.Http;

using com.lifepixer.mangaplex.Server.Media;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Knob 3 (1.5.0): MangaPlex:Media:MaxConcurrentJobs is now bound from
/// configuration in Program.cs (previously hardcoded to the
/// WorkerPoolOptions default of 2, with no override path). Boots the full
/// host via WebApplicationFactory (no HTTP request needed) to verify the
/// value that DI actually resolves.
/// </summary>
public sealed class WorkerConcurrencyConfigTests
{
    [Fact]
    public async Task MaxConcurrentJobs_BoundFromConfig_OverridesDefault()
    {
        Environment.SetEnvironmentVariable("MangaPlex__Media__MaxConcurrentJobs", "5");
        try
        {
            await using var factory = new MangaPlexWebApplicationFactory();
            var options = factory.Services.GetRequiredService<WorkerPoolOptions>();
            Assert.Equal(5, options.MaxConcurrentJobs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MangaPlex__Media__MaxConcurrentJobs", null);
        }
    }

    [Fact]
    public async Task MaxConcurrentJobs_Unset_KeepsDefaultOfTwo()
    {
        Environment.SetEnvironmentVariable("MangaPlex__Media__MaxConcurrentJobs", null);
        await using var factory = new MangaPlexWebApplicationFactory();
        var options = factory.Services.GetRequiredService<WorkerPoolOptions>();
        Assert.Equal(2, options.MaxConcurrentJobs);
    }

    [Fact]
    public async Task MaxConcurrentJobs_InvalidValue_KeepsDefaultOfTwo()
    {
        Environment.SetEnvironmentVariable("MangaPlex__Media__MaxConcurrentJobs", "not-a-number");
        try
        {
            await using var factory = new MangaPlexWebApplicationFactory();
            var options = factory.Services.GetRequiredService<WorkerPoolOptions>();
            Assert.Equal(2, options.MaxConcurrentJobs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MangaPlex__Media__MaxConcurrentJobs", null);
        }
    }
}
