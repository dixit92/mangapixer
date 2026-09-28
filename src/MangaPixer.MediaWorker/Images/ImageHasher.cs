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
/// decodes them. The first frame is flattened onto white, turned to grayscale, squeezed to 32x32 (aspect
/// ignored, Lanczos - the most crop-tolerant of the kernels calibrated on public covers) and handed to <see cref="CoverHash.Compute"/>. Size and dimension caps are
/// checked before the pixels are decoded. Never writes anything.
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

    public static HashedImage Hash(byte[] bytes, int maxDimension)
    {
        try
        {
            var probe = new MagickImageInfo(bytes);
            if (probe.Width == 0 || probe.Height == 0)
                return HashedImage.Failed(ImageHashErrors.DecodeFailed);
            if (probe.Width > (uint)maxDimension || probe.Height > (uint)maxDimension)
                return HashedImage.Failed(ImageHashErrors.TooLarge);

            using var image = new MagickImage(bytes);
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
