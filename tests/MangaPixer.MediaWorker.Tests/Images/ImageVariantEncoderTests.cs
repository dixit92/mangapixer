namespace com.lifepixer.mangapixer.Tests.MediaWorker.Images;

using com.lifepixer.mangapixer.MediaWorker.Images;
using ImageMagick;
using Xunit;

/// <summary>
/// Tests for sized page-variant encoding (1.19.0). The reader can ask for a
/// page sized to its display ("webp@1440:balanced"); the worker must downscale
/// with the requested kernel, preserve aspect ratio, and never upscale.
///
/// Fixtures are generated in-process with Magick.NET (a flat PNG compresses to
/// a few KB even at 3000x2000) rather than committed, following the synthetic
/// fixture rule used by ImageProbeAdapterTests.
/// </summary>
public sealed class ImageVariantEncoderTests : IDisposable
{
    private readonly ImageVariantEncoder _encoder = new();
    private readonly string _scratch;

    public ImageVariantEncoderTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "mangapixer-encoder-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Encode_SizedVariant_DownscalesLongestEdgeAndPreservesAspect()
    {
        var source = CreatePng(3000, 2000);
        var output = OutputPath();

        var result = _encoder.Encode(source, "webp@1440", output, thumbnailMaxDimension: 320, webpQuality: 82, pageMaxDimension: 1440);

        Assert.Equal("image/webp", result.MediaType);
        Assert.Equal(1440, result.Width);
        // 3000x2000 is 3:2; the 1440 box yields 1440x960.
        Assert.Equal(960, result.Height);

        var (w, h) = Probe(output);
        Assert.Equal(1440, w);
        Assert.Equal(960, h);
    }

    [Fact]
    public void Encode_SizedVariant_PortraitSourceUsesLongestEdge()
    {
        var source = CreatePng(1600, 2400);
        var output = OutputPath();

        var result = _encoder.Encode(source, "webp@1080", output, thumbnailMaxDimension: 320, webpQuality: 82, pageMaxDimension: 1080);

        // Height is the longest edge, so it is the one clamped to 1080.
        Assert.Equal(1080, result.Height);
        Assert.Equal(720, result.Width);
    }

    [Fact]
    public void Encode_SizedVariant_SmallerSourceIsNotUpscaled()
    {
        var source = CreatePng(800, 600);
        var output = OutputPath();

        var result = _encoder.Encode(source, "webp@2160", output, thumbnailMaxDimension: 320, webpQuality: 82, pageMaxDimension: 2160);

        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
    }

    [Fact]
    public void Encode_FullWebpVariant_IsUnchangedWhenNoMaxDimensionIsGiven()
    {
        var source = CreatePng(3000, 2000);
        var output = OutputPath();

        // The pre-1.19.0 call shape: no pageMaxDimension at all.
        var result = _encoder.Encode(source, "webp", output, thumbnailMaxDimension: 320, webpQuality: 82);

        Assert.Equal(3000, result.Width);
        Assert.Equal(2000, result.Height);
    }

    [Fact]
    public void Encode_OriginalVariant_IgnoresMaxDimension()
    {
        var source = CreatePng(3000, 2000);
        var output = OutputPath();

        var result = _encoder.Encode(source, "original", output, thumbnailMaxDimension: 320, webpQuality: 82, pageMaxDimension: 1080);

        Assert.Equal("application/octet-stream", result.MediaType);
        Assert.Equal(3000, result.Width);
        Assert.Equal(2000, result.Height);
        Assert.Equal(source, File.ReadAllBytes(output));
    }

    [Fact]
    public void Encode_SizedVariant_EveryFilterYieldsSameSizeButDifferentBytes()
    {
        // A fine checkerboard is the worst case the filter choice exists for:
        // it is pure high-frequency energy, exactly like the screentone that
        // beats against the browser's second resample. If the three kernels
        // were not actually being applied, their output bytes would be equal.
        var source = CreateCheckerboardPng(3000, 2000, cell: 3);

        var bytes = new Dictionary<string, byte[]>();
        foreach (var filter in new[] { "sharp", "balanced", "soft" })
        {
            var output = OutputPath();
            var result = _encoder.Encode(
                source, "webp@1080:" + filter, output,
                thumbnailMaxDimension: 320, webpQuality: 82,
                pageMaxDimension: 1080, resizeFilter: filter);

            // The kernel changes the pixels, never the geometry.
            Assert.Equal(1080, result.Width);
            Assert.Equal(720, result.Height);
            bytes[filter] = File.ReadAllBytes(output);
        }

        Assert.NotEqual(bytes["sharp"], bytes["balanced"]);
        Assert.NotEqual(bytes["balanced"], bytes["soft"]);
        Assert.NotEqual(bytes["sharp"], bytes["soft"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lanczos")]
    [InlineData("SHARP")]
    public void Encode_SizedVariant_UnusableFilterFallsBackToSharp(string? filter)
    {
        // Null/unknown degrade to the pre-1.20.0 Lanczos path rather than
        // failing, so a version-skewed worker still serves the page. "SHARP"
        // is here because normalisation is case-insensitive: it must produce
        // the sharp kernel, not the fallback by accident.
        var source = CreateCheckerboardPng(3000, 2000, cell: 3);

        var fallbackOutput = OutputPath();
        _encoder.Encode(source, "webp@1080", fallbackOutput,
            thumbnailMaxDimension: 320, webpQuality: 82,
            pageMaxDimension: 1080, resizeFilter: filter);

        var sharpOutput = OutputPath();
        _encoder.Encode(source, "webp@1080:sharp", sharpOutput,
            thumbnailMaxDimension: 320, webpQuality: 82,
            pageMaxDimension: 1080, resizeFilter: "sharp");

        Assert.Equal(File.ReadAllBytes(sharpOutput), File.ReadAllBytes(fallbackOutput));
    }

    [Fact]
    public void Encode_ThumbnailVariant_IgnoresTheResizeFilter()
    {
        // Thumbnails stay Lanczos whatever the page filter is: changing them
        // would invalidate every cached thumbnail for no reader-visible gain.
        var source = CreateCheckerboardPng(3000, 2000, cell: 3);

        var softOutput = OutputPath();
        _encoder.Encode(source, "thumbnail", softOutput,
            thumbnailMaxDimension: 320, webpQuality: 82,
            pageMaxDimension: 1080, resizeFilter: "soft");

        var defaultOutput = OutputPath();
        _encoder.Encode(source, "thumbnail", defaultOutput,
            thumbnailMaxDimension: 320, webpQuality: 82,
            pageMaxDimension: 1080);

        Assert.Equal(File.ReadAllBytes(defaultOutput), File.ReadAllBytes(softOutput));
    }

    [Fact]
    public void Encode_ThumbnailVariant_StillUsesThumbnailBound()
    {
        var source = CreatePng(3000, 2000);
        var output = OutputPath();

        // A stray pageMaxDimension must not hijack the thumbnail bound.
        var result = _encoder.Encode(source, "thumbnail", output, thumbnailMaxDimension: 320, webpQuality: 82, pageMaxDimension: 1440);

        Assert.Equal(320, result.Width);
        // 3:2 into a 320 box is 213.33 high; allow for rounding either way.
        Assert.InRange(result.Height, 212, 214);
    }

    // --- Helpers ---

    private string OutputPath() =>
        Path.Combine(_scratch, "out-" + Guid.NewGuid().ToString("N")[..8] + ".bin");

    /// <summary>
    /// Synthetic PNG with a horizontal gradient, so a resize has real detail to
    /// filter rather than a single flat colour.
    /// </summary>
    private static byte[] CreatePng(int width, int height)
    {
        using var image = new MagickImage(MagickColors.White, (uint)width, (uint)height);
        using var gradient = new MagickImage("gradient:black-white", (uint)width, (uint)height);
        image.Composite(gradient, CompositeOperator.Over);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    /// <summary>
    /// Synthetic PNG of a fine checkerboard - a stand-in for a screentone: a
    /// near-Nyquist pattern where different resampling kernels visibly (and
    /// therefore byte-wise) disagree. A gradient would not distinguish them.
    /// </summary>
    private static byte[] CreateCheckerboardPng(int width, int height, int cell)
    {
        // Written as raw 8-bit grayscale and read back, rather than poked in
        // pixel by pixel: one allocation and no per-pixel Magick.NET calls for
        // six million pixels.
        var raw = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                raw[(y * width) + x] = ((x / cell) + (y / cell)) % 2 == 0 ? (byte)0 : (byte)255;
        }

        var settings = new MagickReadSettings
        {
            Format = MagickFormat.Gray,
            Width = (uint)width,
            Height = (uint)height,
            Depth = 8,
        };
        using var image = new MagickImage(raw, settings);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    private static (int Width, int Height) Probe(string path)
    {
        var info = new MagickImageInfo(path);
        return ((int)info.Width, (int)info.Height);
    }
}
