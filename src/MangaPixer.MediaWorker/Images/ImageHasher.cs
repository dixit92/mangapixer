namespace com.lifepixer.mangapixer.MediaWorker.Images;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using ImageMagick;

/// <summary>The outcome of hashing one image: the hash and the decoded size, or an <see cref="ImageHashErrors"/> code.</summary>
public sealed record HashedImage(ulong Hash, int Width, int Height, string? Error)
{
    public static HashedImage Failed(string error) => new(0, 0, 0, error);
}

/// <summary>
/// The 64-bit perceptual hash of an image file (protocol v4 <c>image_hash</c>, 1.28.0): decoded with Magick.NET
/// HERE, in the worker - the untrusted-input boundary; provider images are remote bytes and the server never
/// decodes them. Only JPEG, PNG, GIF and WebP are decoded (<see cref="SniffFormat"/>, then the probed format, then a
/// decode pinned to that format). The first frame is flattened onto white, turned to grayscale, squeezed to 32x32
/// (aspect ignored, Lanczos - the most crop-tolerant kernel in the calibration) and handed to
/// <see cref="CoverHash.Compute"/>. Size and dimension caps are checked before the pixels are decoded. Never writes
/// anything.
/// </summary>
public static class ImageHasher
{
    public static HashedImage HashFile(string path, long maxBytes, int maxDimension)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return HashedImage.Failed(ImageHashErrors.Missing);
        if (info.Length == 0 || info.Length > maxBytes)
            return HashedImage.Failed(ImageHashErrors.TooLarge);
        return Hash(File.ReadAllBytes(path), maxDimension);
    }

    /// <summary>
    /// The raster format the bytes announce by their magic numbers - JPEG, PNG, GIF or WebP - or null. Anything else (SVG,
    /// MVG, PostScript, text, ...) is refused before ImageMagick sees a byte of it: those coders can read files or run
    /// drawing programs, and a provider image or a thumbnail is never one of them.
    /// </summary>
    public static MagickFormat? SniffFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return MagickFormat.Jpeg;
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return MagickFormat.Png;
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return MagickFormat.Gif;
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return MagickFormat.WebP;
        return null;
    }

    /// <summary>The formats <see cref="Hash"/> decodes (what the probe may report for them).</summary>
    private static bool IsAllowed(MagickFormat format) => format is MagickFormat.Jpeg or MagickFormat.Jpg or MagickFormat.Png
        or MagickFormat.Png8 or MagickFormat.Png24 or MagickFormat.Png32 or MagickFormat.Png48 or MagickFormat.Png64
        or MagickFormat.Gif or MagickFormat.Gif87 or MagickFormat.WebP;

    public static HashedImage Hash(byte[] bytes, int maxDimension)
    {
        // Defence in depth (1.28.0 review): magic bytes first, then the probe's format, then a decode pinned to that format.
        if (SniffFormat(bytes) is not { } sniffed)
            return HashedImage.Failed(ImageHashErrors.DecodeFailed);
        try
        {
            var settings = new MagickReadSettings { Format = sniffed };
            var probe = new MagickImageInfo(bytes, settings);
            if (!IsAllowed(probe.Format))
                return HashedImage.Failed(ImageHashErrors.DecodeFailed);
            if (probe.Width == 0 || probe.Height == 0)
                return HashedImage.Failed(ImageHashErrors.DecodeFailed);
            if (probe.Width > (uint)maxDimension || probe.Height > (uint)maxDimension)
                return HashedImage.Failed(ImageHashErrors.TooLarge);

            using var image = new MagickImage(bytes, settings);
            var (width, height) = ((int)image.Width, (int)image.Height);
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
            image.Grayscale(PixelIntensityMethod.Rec709Luma);
            image.FilterType = FilterType.Lanczos;
            image.Resize(new MagickGeometry(CoverHash.Side, CoverHash.Side) { IgnoreAspectRatio = true });
            using var pixels = image.GetPixels();
            var gray = pixels.ToByteArray(PixelMapping.RGB)
                ?? throw new InvalidDataException("no pixels");
            var luma = new byte[CoverHash.Side * CoverHash.Side];
            for (var i = 0; i < luma.Length; i++)
                luma[i] = gray[i * 3];
            return new HashedImage(CoverHash.Compute(luma), width, height, null);
        }
        catch (MagickException)
        {
            return HashedImage.Failed(ImageHashErrors.DecodeFailed);
        }
        catch (InvalidDataException)
        {
            return HashedImage.Failed(ImageHashErrors.DecodeFailed);
        }
    }
}
