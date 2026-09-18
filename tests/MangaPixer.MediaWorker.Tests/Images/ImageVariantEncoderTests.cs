namespace com.lifepixer.mangapixer.Tests.MediaWorker.Images;

using com.lifepixer.mangapixer.MediaWorker.Images;
using ImageMagick;
using Xunit;

/// <summary>
/// Tests for sized page-variant encoding (1.19.0). The reader can ask for a
/// page sized to its display ("webp@1440"); the worker must downscale with
/// Lanczos, preserve aspect ratio, and never upscale.
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

    private static (int Width, int Height) Probe(string path)
    {
        var info = new MagickImageInfo(path);
        return ((int)info.Width, (int)info.Height);
    }
}
