namespace com.lifepixer.mangaplex.MediaWorker.Images;

using com.lifepixer.mangaplex.Core.Media;
using ImageMagick;

/// <summary>
/// Result of probing an image file.
/// </summary>
public sealed record ImageProbeResult
{
    public required string MediaType { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required AnimationState AnimationState { get; init; }
    public required bool HasAlpha { get; init; }
    public required int FrameCount { get; init; }
    public string? Error { get; init; }
    public bool IsSupported => Error is null;
}

/// <summary>
/// Image probing and validation adapter using Magick.NET Q8.
/// Probes image format, dimensions, animation state, and alpha channel.
/// Transcoding to output variants lives in ImageVariantEncoder; enforcing
/// acceptance rules (e.g. rejecting unsupported formats) beyond a basic
/// probe is not yet implemented here.
/// </summary>
public sealed class ImageProbeAdapter : IDisposable
{
    /// <summary>
    /// Probes an image from a stream. Does not fully decode — reads header/metadata only.
    /// </summary>
    public ImageProbeResult Probe(Stream stream)
    {
        if (stream is null || !stream.CanRead || stream.Length == 0)
        {
            return new ImageProbeResult
            {
                MediaType = "unknown",
                Width = 0,
                Height = 0,
                AnimationState = AnimationState.Unknown,
                HasAlpha = false,
                FrameCount = 0,
                Error = "Stream is null, unreadable, or empty",
            };
        }

        try
        {
            // Read with MagickImageInfo (header-only probe)
            var info = new MagickImageInfo(stream);

            var format = info.Format;
            var width = (int)info.Width;
            var height = (int)info.Height;

            // Determine media type
            var mediaType = MapMediaType(format);

            // Determine animation state — requires checking if the format supports
            // multiple frames. MagickImageInfo doesn't tell us frame count,
            // so we check the format.
            var animationState = DetermineAnimationState(format);

            // Check alpha — requires a full read for some formats.
            // A header-only probe can't confirm alpha is actually used, so we
            // check the format's capability instead (see ReadWithFrames for a
            // fully-decoded, exact check).
            var hasAlpha = format == MagickFormat.Png ||
                          format == MagickFormat.Gif ||
                          format == MagickFormat.WebP ||
                          format == MagickFormat.Avif;

            return new ImageProbeResult
            {
                MediaType = mediaType,
                Width = width,
                Height = height,
                AnimationState = animationState,
                HasAlpha = hasAlpha,
                FrameCount = 1, // Info doesn't give frame count
            };
        }
        catch (MagickException ex)
        {
            return new ImageProbeResult
            {
                MediaType = "unknown",
                Width = 0,
                Height = 0,
                AnimationState = AnimationState.Unknown,
                HasAlpha = false,
                FrameCount = 0,
                Error = ex.GetType().Name + ": " + ex.Message,
            };
        }
        catch (ArgumentException ex)
        {
            // Magick.NET throws ArgumentException for empty/invalid streams
            return new ImageProbeResult
            {
                MediaType = "unknown",
                Width = 0,
                Height = 0,
                AnimationState = AnimationState.Unknown,
                HasAlpha = false,
                FrameCount = 0,
                Error = ex.GetType().Name + ": " + ex.Message,
            };
        }
    }

    /// <summary>
    /// Probes an image from a byte array.
    /// </summary>
    public ImageProbeResult Probe(byte[] data)
    {
        using var ms = new MemoryStream(data);
        return Probe(ms);
    }

    /// <summary>
    /// Fully reads an image and returns frame count and animation info.
    /// This is more expensive than Probe — use only when animation details are needed.
    /// </summary>
    public ImageProbeResult ReadWithFrames(Stream stream)
    {
        try
        {
            using var collection = new MagickImageCollection(stream);
            var frameCount = collection.Count;
            var firstFrame = collection.FirstOrDefault();
            if (firstFrame is null)
            {
                return new ImageProbeResult
                {
                    MediaType = "unknown",
                    Width = 0,
                    Height = 0,
                    AnimationState = AnimationState.Unknown,
                    HasAlpha = false,
                    FrameCount = 0,
                    Error = "No frames in image",
                };
            }

            var format = firstFrame.Format;
            var animationState = frameCount > 1 ? AnimationState.Animated : AnimationState.NotAnimated;

            return new ImageProbeResult
            {
                MediaType = MapMediaType(format),
                Width = (int)firstFrame.Width,
                Height = (int)firstFrame.Height,
                AnimationState = animationState,
                HasAlpha = firstFrame.HasAlpha,
                FrameCount = frameCount,
            };
        }
        catch (MagickException ex)
        {
            return new ImageProbeResult
            {
                MediaType = "unknown",
                Width = 0,
                Height = 0,
                AnimationState = AnimationState.Unknown,
                HasAlpha = false,
                FrameCount = 0,
                Error = ex.GetType().Name + ": " + ex.Message,
            };
        }
    }

    private static string MapMediaType(MagickFormat format) => format switch
    {
        MagickFormat.Jpeg or MagickFormat.Jpg => ImageMediaTypes.Jpeg,
        MagickFormat.Png => ImageMediaTypes.Png,
        MagickFormat.WebP => ImageMediaTypes.WebP,
        MagickFormat.Avif => ImageMediaTypes.Avif,
        MagickFormat.Gif => ImageMediaTypes.Gif,
        MagickFormat.Bmp or MagickFormat.Bmp3 => ImageMediaTypes.Bmp,
        MagickFormat.Tiff => ImageMediaTypes.Tiff,
        _ => $"image/x-{format.ToString().ToLowerInvariant()}",
    };

    private static AnimationState DetermineAnimationState(MagickFormat format)
    {
        // These formats CAN be animated; actual animation state requires frame count check
        return format switch
        {
            MagickFormat.Gif => AnimationState.Unknown, // Could be animated or static
            MagickFormat.Png => AnimationState.Unknown, // APNG is possible
            MagickFormat.WebP => AnimationState.Unknown, // Animated WebP is possible
            MagickFormat.Avif => AnimationState.Unknown, // Animated AVIF is possible
            _ => AnimationState.NotAnimated,
        };
    }

    public void Dispose()
    {
        // Magick.NET resources are disposed per-call in this adapter
    }
}
