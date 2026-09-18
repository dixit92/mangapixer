namespace com.lifepixer.mangapixer.MediaWorker.Images;

using com.lifepixer.mangapixer.Core.Media;
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
///  - "webp@&lt;n&gt;:&lt;filter&gt;": WebP downscaled so the longest edge ≤ pageMaxDimension
///                 (1.19.0), with a selectable resampling kernel (1.20.0). The
///                 bucket and filter in the name are informational; the size and
///                 kernel come from the parameters.
///  - "thumbnail": WebP downscaled so the longest edge ≤ thumbnailMaxDimension, using a
///                 high-quality Lanczos filter (good for line art; avoids the
///                 mushy default and screentone moiré on downscale).
///  - "original":  verbatim source bytes, no transcode.
///
/// Downscale never becomes upscale: a source already within the requested bound is
/// encoded at its native size.
///
/// Animated sources (multi-frame GIF/WebP/APNG) are passed through verbatim for the
/// full-size variant so animation is preserved (criterion 5); their thumbnail uses
/// the first frame only.
/// </summary>
public sealed class ImageVariantEncoder
{
    /// <summary>
    /// Prefix shared by the full-size and sized page variants ("webp", "webp@1440").
    /// </summary>
    private const string WebpVariantPrefix = "webp";

    /// <summary>
    /// Encodes one page variant.
    /// </summary>
    /// <param name="source">Raw source entry bytes.</param>
    /// <param name="variant">"original", "thumbnail", "webp", or "webp@&lt;n&gt;".</param>
    /// <param name="outputPath">Server-owned path to write the encoded bytes to.</param>
    /// <param name="thumbnailMaxDimension">Longest edge (px) for the "thumbnail" variant.</param>
    /// <param name="webpQuality">WebP quality (1-100).</param>
    /// <param name="pageMaxDimension">
    /// Longest edge (px) for sized page variants; 0 (default) means no resize, i.e.
    /// the pre-1.19.0 full-size transcode.
    /// </param>
    /// <param name="resizeFilter">
    /// Resampling kernel for sized page variants only: a
    /// <see cref="PageVariantFilters"/> name. Null (default) or an unrecognised
    /// name means Lanczos, so a version-skewed or malformed request degrades to
    /// the pre-1.20.0 behaviour rather than failing. The thumbnail bound keeps
    /// Lanczos unconditionally: thumbnails are tiny, sharpness matters more than
    /// screentone beating at that size, and changing them would invalidate every
    /// cached thumbnail for no reader-visible gain.
    /// </param>
    public EncodedVariant Encode(byte[] source, string variant, string outputPath, int thumbnailMaxDimension, int webpQuality, int pageMaxDimension = 0, string? resizeFilter = null)
    {
        if (variant == "original")
        {
            File.WriteAllBytes(outputPath, source);
            var (w, h) = ProbeDimensions(source);
            return new EncodedVariant { MediaType = "application/octet-stream", Width = w, Height = h, ByteSize = source.Length };
        }

        var frameCount = CountFrames(source);
        var animated = frameCount > 1;

        // Preserve animation for the page variants by passing the source through
        // untouched — a single-frame re-encode would freeze it. This deliberately
        // also covers sized variants ("webp@1440"): an animation is never resized,
        // so a sized request for an animated page yields the original animation.
        if (animated && variant.StartsWith(WebpVariantPrefix, StringComparison.Ordinal))
        {
            File.WriteAllBytes(outputPath, source);
            var (w, h) = ProbeDimensions(source);
            return new EncodedVariant { MediaType = GuessAnimatedMediaType(source), Width = w, Height = h, ByteSize = source.Length };
        }

        using var image = new MagickImage(source);
        // Flatten to the first frame for statics/thumbnails (defensive for odd inputs).
        image.Format = MagickFormat.WebP;
        image.Quality = (uint)Math.Clamp(webpQuality, 1, 100);

        // One resize rule for both downscaling variants: the thumbnail uses the
        // thumbnail bound, a sized page variant uses the bucket bound, and the
        // full-size "webp" variant passes 0 and is left alone.
        var resizeBound = variant == "thumbnail"
            ? thumbnailMaxDimension
            : variant.StartsWith(WebpVariantPrefix, StringComparison.Ordinal) ? pageMaxDimension : 0;

        if (resizeBound > 0 &&
            (image.Width > (uint)resizeBound || image.Height > (uint)resizeBound))
        {
            // The thumbnail is always Lanczos; only a sized page variant honours
            // the requested kernel.
            image.FilterType = variant == "thumbnail"
                ? FilterType.Lanczos
                : MapFilter(resizeFilter);
            // Greater=only shrink; aspect ratio preserved by a single WxH box.
            image.Resize(new MagickGeometry((uint)resizeBound, (uint)resizeBound) { Greater = true });
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

    /// <summary>
    /// Maps the public filter vocabulary onto Magick.NET kernels. This is the
    /// only place the two are tied together - the names travel over HTTP and the
    /// worker protocol as plain strings so no other assembly needs Magick.NET.
    ///
    /// Box is genuine area averaging on a downscale, which is why it is the
    /// "soft" option: it integrates the halftone dots away instead of ringing
    /// against them. Mitchell sits between that and Lanczos's sharp, slightly
    /// ringing reconstruction.
    ///
    /// Unknown and null both fall back to Lanczos rather than throwing: the
    /// server validates the vocabulary at its own boundary, so anything odd
    /// arriving here is a version skew, and serving a slightly-too-sharp page
    /// beats failing the request.
    /// </summary>
    private static FilterType MapFilter(string? resizeFilter) =>
        PageVariantFilters.TryNormalize(resizeFilter, out var name)
            ? name switch
            {
                PageVariantFilters.Balanced => FilterType.Mitchell,
                PageVariantFilters.Soft => FilterType.Box,
                _ => FilterType.Lanczos,
            }
            : FilterType.Lanczos;

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
