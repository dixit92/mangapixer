namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Server.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Tests for the server-authoritative page-variant bucket ladder (1.19.0):
/// defaults, snap-up selection, and startup validation of a malformed ladder.
/// </summary>
public sealed class PageVariantOptionsTests
{
    [Fact]
    public void Defaults_AreTheThreeBucketLadder()
    {
        var options = new PageVariantOptions();

        Assert.Equal(new[] { 1080, 1440, 2160 }, options.MaxDimensions);
        Assert.Equal(82, options.WebpQuality);
        // 1.20.0 moved the default off Lanczos ("sharp") deliberately.
        Assert.Equal("balanced", options.DefaultFilter);
        options.Validate();
    }

    [Theory]
    [InlineData(1, 1080)]
    [InlineData(1080, 1080)]
    [InlineData(1081, 1440)]
    [InlineData(1300, 1440)]
    [InlineData(1440, 1440)]
    [InlineData(2160, 2160)]
    public void SelectBucket_SnapsUpToTheSmallestCoveringBucket(int requested, int expected)
    {
        var options = new PageVariantOptions();

        Assert.Equal(expected, options.SelectBucket(requested));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2161)]
    [InlineData(99999)]
    public void SelectBucket_ReturnsNullForAbsentOrAboveLadderRequests(int requested)
    {
        var options = new PageVariantOptions();

        Assert.Null(options.SelectBucket(requested));
    }

    [Fact]
    public void SelectBucket_EmptyLadderDisablesSizedVariants()
    {
        var options = new PageVariantOptions { MaxDimensions = [] };
        options.Validate();

        Assert.Null(options.SelectBucket(1440));
    }

    [Theory]
    [InlineData(1440, "balanced", "webp@1440:balanced")]
    [InlineData(2160, "sharp", "webp@2160:sharp")]
    [InlineData(1080, "soft", "webp@1080:soft")]
    public void VariantName_IsTheCacheDistinctBucketAndFilterVariant(int bucket, string filter, string expected)
    {
        Assert.Equal(expected, PageVariantOptions.VariantName(bucket, filter));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    public void VariantName_FallsBackToSharpForUnusableFilters(string? filter)
    {
        // Callers validate first; this only guarantees the string stays well
        // formed, and the fallback matches the encoder's own null/unknown rule.
        Assert.Equal("webp@1440:sharp", PageVariantOptions.VariantName(1440, filter));
    }

    [Theory]
    [InlineData("sharp")]
    [InlineData("balanced")]
    [InlineData("soft")]
    public void Validate_AcceptsEveryFilterInTheVocabulary(string filter)
    {
        var options = new PageVariantOptions { DefaultFilter = filter };

        options.Validate();

        Assert.Equal(filter, options.DefaultFilter);
    }

    [Theory]
    [InlineData("Balanced")]
    [InlineData("  SOFT  ")]
    public void Validate_NormalisesFilterCaseAndWhitespace(string configured)
    {
        // The value reaches a cache key and a response header, so it is folded
        // to the canonical lower-case spelling instead of being rejected: a
        // capitalised config value is a reasonable thing for a human to write.
        var options = new PageVariantOptions { DefaultFilter = configured };

        options.Validate();

        Assert.Equal(configured.Trim().ToLowerInvariant(), options.DefaultFilter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lanczos")]
    [InlineData("crisp")]
    public void Validate_RejectsUnknownDefaultFilter(string filter)
    {
        var options = new PageVariantOptions { DefaultFilter = filter };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(new[] { 1080, 0, 2160 })]       // non-positive entry
    [InlineData(new[] { -1080 })]               // negative entry
    [InlineData(new[] { 2160, 1440, 1080 })]    // descending
    [InlineData(new[] { 1080, 1080, 2160 })]    // duplicate
    [InlineData(new[] { 1, 2, 3, 4, 5, 6, 7 })] // longer than MaxLadderLength
    public void Validate_RejectsMalformedLadders(int[] ladder)
    {
        var options = new PageVariantOptions { MaxDimensions = ladder };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_RejectsOutOfRangeQuality(int quality)
    {
        var options = new PageVariantOptions { WebpQuality = quality };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Registration_BindsLadderFromConfiguration()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["MangaPixer:Media:PageVariants:MaxDimensions:0"] = "800",
            ["MangaPixer:Media:PageVariants:MaxDimensions:1"] = "1600",
            ["MangaPixer:Media:PageVariants:WebpQuality"] = "70",
            ["MangaPixer:Media:PageVariants:DefaultFilter"] = "soft",
        });

        var options = provider.GetRequiredService<PageVariantOptions>();

        Assert.Equal(new[] { 800, 1600 }, options.MaxDimensions);
        Assert.Equal(70, options.WebpQuality);
        Assert.Equal("soft", options.DefaultFilter);
    }

    [Fact]
    public void Registration_ThrowsOnUnknownDefaultFilter()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["MangaPixer:Media:PageVariants:DefaultFilter"] = "lanczos",
        });

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<PageVariantOptions>());
    }

    [Fact]
    public void Registration_AcceptsCommaSeparatedLadder()
    {
        // Indexed array keys are painful as environment variables, so a single
        // comma-separated value is accepted too.
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["MangaPixer:Media:PageVariants:MaxDimensions"] = "900, 1800",
        });

        var options = provider.GetRequiredService<PageVariantOptions>();

        Assert.Equal(new[] { 900, 1800 }, options.MaxDimensions);
    }

    [Fact]
    public void Registration_UsesDefaultsWhenSectionIsAbsent()
    {
        var provider = BuildProvider(new Dictionary<string, string?>());

        var options = provider.GetRequiredService<PageVariantOptions>();

        Assert.Equal(new[] { 1080, 1440, 2160 }, options.MaxDimensions);
        Assert.Equal(PageVariantFilters.Balanced, options.DefaultFilter);
    }

    [Fact]
    public void Registration_ThrowsOnMalformedLadder()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["MangaPixer:Media:PageVariants:MaxDimensions"] = "2160, 1080",
        });

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<PageVariantOptions>());
    }

    [Fact]
    public void Registration_ThrowsOnNonIntegerLadderEntry()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["MangaPixer:Media:PageVariants:MaxDimensions"] = "1080, wide",
        });

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<PageVariantOptions>());
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddMangaPixerMedia(o =>
        {
            var root = Path.Combine(Path.GetTempPath(), "mangapixer-pvo-" + Guid.NewGuid().ToString("N")[..8]);
            o.CacheRoot = Path.Combine(root, "cache");
            o.ScratchRoot = Path.Combine(root, "scratch");
        });
        return services.BuildServiceProvider();
    }
}
