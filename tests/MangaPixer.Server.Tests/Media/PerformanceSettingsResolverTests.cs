namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>Performance settings precedence (1.42.0): configuration, then the admin setting, then the default.</summary>
public sealed class PerformanceSettingsResolverTests
{
    private static PerformanceConfiguration Config(params (string Key, string Value)[] pairs) =>
        PerformanceConfiguration.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build());

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 2)]
    [InlineData(8, 2)]
    [InlineData(12, 3)]
    [InlineData(16, 4)]
    [InlineData(64, 4)]
    public void DefaultWorkers_IsAQuarterOfTheProcessors_BetweenTwoAndFour(int processors, int expected) =>
        Assert.Equal(expected, PerformanceSettingsResolver.DefaultWorkersFor(processors));

    [Fact]
    public void NothingSet_GivesTheDefaults()
    {
        var e = PerformanceSettingsResolver.Compute(Config(), null, processorCount: 16);
        Assert.Equal((4, "default"), (e.MediaWorkers, e.MediaWorkersSource));
        Assert.Equal((1L << 30, "default"), (e.PageCacheBytes, e.PageCacheSource));
        Assert.Equal((BackgroundWhileReading.Slow, "default"), (e.BackgroundWhileReading, e.BackgroundWhileReadingSource));
    }

    [Fact]
    public void AdminSetting_BeatsTheDefault_AndConfigurationBeatsBoth()
    {
        var row = new AppSettingsEntity { PerformanceMediaWorkers = 6, PerformancePageCacheBytes = 2L << 30, PerformanceBackgroundWhileReading = 0 };

        var stored = PerformanceSettingsResolver.Compute(Config(), row, processorCount: 16);
        Assert.Equal((6, "settings"), (stored.MediaWorkers, stored.MediaWorkersSource));
        Assert.Equal((2L << 30, "settings"), (stored.PageCacheBytes, stored.PageCacheSource));
        Assert.Equal((BackgroundWhileReading.Pause, "settings"), (stored.BackgroundWhileReading, stored.BackgroundWhileReadingSource));

        var pinned = PerformanceSettingsResolver.Compute(Config(
            (PerformanceConfiguration.WorkersKey, "3"),
            (PerformanceConfiguration.PageCacheKey, "536870912"),
            (PerformanceConfiguration.BackgroundKey, "Normal")), row, processorCount: 16);
        Assert.Equal((3, "configuration"), (pinned.MediaWorkers, pinned.MediaWorkersSource));
        Assert.Equal((536870912L, "configuration"), (pinned.PageCacheBytes, pinned.PageCacheSource));
        Assert.Equal((BackgroundWhileReading.Normal, "configuration"), (pinned.BackgroundWhileReading, pinned.BackgroundWhileReadingSource));
    }

    [Fact]
    public void InvalidConfiguration_IsIgnored_AndNamed_AndOutOfRangeStoredValuesFallBack()
    {
        var config = Config((PerformanceConfiguration.WorkersKey, "lots"), (PerformanceConfiguration.BackgroundKey, "sometimes"));
        Assert.Equal(2, config.InvalidKeys.Count);

        var row = new AppSettingsEntity { PerformanceMediaWorkers = 99, PerformancePageCacheBytes = 1024, PerformanceBackgroundWhileReading = 7 };
        var e = PerformanceSettingsResolver.Compute(config, row, processorCount: 8);
        Assert.Equal((2, "default"), (e.MediaWorkers, e.MediaWorkersSource));
        Assert.Equal("default", e.PageCacheSource);
        Assert.Equal("default", e.BackgroundWhileReadingSource);
    }
}
