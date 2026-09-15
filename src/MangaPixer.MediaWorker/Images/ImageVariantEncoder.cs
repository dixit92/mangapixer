namespace com.lifepixer.mangaplex.MediaWorker.Images;

using ImageMagick;

/// <summary>
/// Result of encoding a page variant.
/// </summary>
public sealed record EncodedVariant
{
    public required string MediaType { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required long ByteSize { get; init; }
}

/// <summary>
/// Encodes page-image variants with Magick.NET. Image decoding lives here in
/// the worker — never in the server — because decoding untrusted archive images is
/// the fault-isolation risk the worker boundary exists to contain.
///
/// Variants:
///  - "webp":      full-size WebP transcode (lossy, quality-tunable).
///  - "thumbnail": WebP downscaled so the longest edge ≤ maxDimension, using a
///                 high-quality Lanczos filter (good for line art; avoids the
///                 mushy default and screentone moiré on downscale).
///  - "original":  verbatim source bytes, no transcode.
///
/// Animated sources (multi-frame GIF/WebP/APNG) are passed through verbatim for the
/// full-size variant so animation is preserved (criterion 5); their thumbnail uses
/// the first frame only.
/// </summary>
public sealed class ImageVariantEncoder
{
    public EncodedVariant Encode(byte[] source, string variant, string outputPath, int maxDimension, int webpQuality)
    {
        if (variant == "original")
        {
            File.WriteAllBytes(outputPath, source);
            var (w, h) = ProbeDimensions(source);
            return new EncodedVariant { MediaType = "application/octet-stream", Width = w, Height = h, ByteSize = source.Length };
        }

        var frameCount = CountFrames(source);
        var animated = frameCount > 1;

        // Preserve animation for the full-size variant by passing the source through
        // untouched — a single-frame re-encode would freeze it.
        if (animated && variant == "webp")
        {
            File.WriteAllBytes(outputPath, source);
            var (w, h) = ProbeDimensions(source);
            return new EncodedVariant { MediaType = GuessAnimatedMediaType(source), Width = w, Height = h, ByteSize = source.Length };
        }

        using var image = new MagickImage(source);
        // Flatten to the first frame for statics/thumbnails (defensive for odd inputs).
        image.Format = MagickFormat.WebP;
        image.Quality = (uint)Math.Clamp(webpQuality, 1, 100);

        if (variant == "thumbnail" && maxDimension > 0 &&
            (image.Width > (uint)maxDimension || image.Height > (uint)maxDimension))
        {
            image.FilterType = FilterType.Lanczos;
            // Greater=only shrink; aspect ratio preserved by a single WxH box.
            image.Resize(new MagickGeometry((uint)maxDimension, (uint)maxDimension) { Greater = true });
        }

        image.Write(outputPath);
        var info = new FileInfo(outputPath);
        return new EncodedVariant
        {
            MediaType = "image/webp",
            Width = (int)image.Width,
            Height = (int)image.Height,
            ByteSize = info.Length,
        };
    }

    private static int CountFrames(byte[] source)
    {
        try
        {
            using var stream = new MemoryStream(source, writable: false);
            var info = new MagickImageInfo(stream);
            // MagickImageInfo doesn't report frame count; a collection read does, but
            // that fully decodes. Keep it cheap: only GIF/WebP/PNG can be animated.
            var fmt = info.Format;
            if (fmt is MagickFormat.Gif or MagickFormat.WebP or MagickFormat.Png or MagickFormat.APng)
            {
                using var coll = new MagickImageCollection(source);
                return coll.Count;
            }
            return 1;
        }
        catch
        {
            return 1;
        }
    }

    private static (int Width, int Height) ProbeDimensions(byte[] source)
    {
        try
        {
            using var stream = new MemoryStream(source, writable: false);
            var info = new MagickImageInfo(stream);
            return ((int)info.Width, (int)info.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static string GuessAnimatedMediaType(byte[] source)
    {
        try
        {
            using var stream = new MemoryStream(source, writable: false);
            var info = new MagickImageInfo(stream);
            return info.Format switch
            {
                MagickFormat.Gif => "image/gif",
                MagickFormat.WebP => "image/webp",
                MagickFormat.Png or MagickFormat.APng => "image/apng",
                _ => "application/octet-stream",
            };
        }
        catch
        {
            return "application/octet-stream";
        }
    }
}
