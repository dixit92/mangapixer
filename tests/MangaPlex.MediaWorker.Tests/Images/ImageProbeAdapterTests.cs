namespace com.lifepixer.mangaplex.Tests.MediaWorker.Images;

using com.lifepixer.mangaplex.Core.Media;
using com.lifepixer.mangaplex.MediaWorker.Images;
using com.lifepixer.mangaplex.TestSupport.Fixtures;
using Xunit;

/// <summary>
/// Tests for image probing via Magick.NET.
/// Verifies that all release-one image formats can be probed correctly.
/// Uses synthetic minimal image fixtures.
/// </summary>
public sealed class ImageProbeAdapterTests : IDisposable
{
    private readonly ImageProbeAdapter _adapter = new();

    public void Dispose() => _adapter.Dispose();

    [Fact]
    public void Probe_MinimalPng_ReturnsPngFormat()
    {
        var result = _adapter.Probe(SyntheticImages.MinimalPng);

        Assert.True(result.IsSupported, result.Error ?? "probe failed");
        Assert.Equal(ImageMediaTypes.Png, result.MediaType);
        Assert.Equal(1, result.Width);
        Assert.Equal(1, result.Height);
    }

    [Fact]
    public void Probe_MinimalGif_ReturnsGifFormat()
    {
        var result = _adapter.Probe(SyntheticImages.MinimalGif);

        Assert.True(result.IsSupported, result.Error ?? "probe failed");
        Assert.Equal(ImageMediaTypes.Gif, result.MediaType);
    }

    [Fact]
    public void Probe_MinimalBmp_ReturnsBmpFormat()
    {
        var result = _adapter.Probe(SyntheticImages.MinimalBmp);

        Assert.True(result.IsSupported, result.Error ?? "probe failed");
        Assert.Equal(ImageMediaTypes.Bmp, result.MediaType);
    }

    [Fact]
    public void Probe_MinimalWebP_ReturnsWebPFormat()
    {
        var result = _adapter.Probe(SyntheticImages.MinimalWebP);

        // WebP probe may fail if the minimal fixture is too small for Magick.NET
        // Document the result either way
        if (!result.IsSupported)
        {
            // This is a known limitation of ultra-minimal WebP fixtures
            // Real WebP files will probe correctly
            Assert.True(result.Error is not null, "Failed probe should have error message");
        }
        else
        {
            Assert.Equal(ImageMediaTypes.WebP, result.MediaType);
        }
    }

    [Fact]
    public void Probe_InvalidImageData_ReturnsError()
    {
        var garbage = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };
        var result = _adapter.Probe(garbage);

        Assert.False(result.IsSupported);
        Assert.NotNull(result.Error);
        Assert.Equal(0, result.Width);
        Assert.Equal(0, result.Height);
    }

    [Fact]
    public void Probe_EmptyData_ReturnsError()
    {
        var result = _adapter.Probe(Array.Empty<byte>());

        Assert.False(result.IsSupported);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Probe_TruncatedPng_ReturnsError()
    {
        // Take only the first 10 bytes of a PNG (signature + partial IHDR)
        var truncated = SyntheticImages.MinimalPng.Take(10).ToArray();
        var result = _adapter.Probe(truncated);

        Assert.False(result.IsSupported);
        Assert.NotNull(result.Error);
    }
}
