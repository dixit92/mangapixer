namespace com.lifepixer.mangapixer.MediaWorker.Images;

using com.lifepixer.mangapixer.Core.WorkerProtocol;
using ImageMagick;

/// <summary>The outcome of rendering one cover: the written thumbnail's size, the source size and the hash, or a <see cref="CoverRenderErrors"/> code.</summary>
public sealed record RenderedCover(int Width, int Height, int SourceWidth, int SourceHeight, ulong? Hash, string? Error)
{
    public static RenderedCover Failed(string error) => new(0, 0, 0, 0, null, error);
}

/// <summary>
/// Renders a cover thumbnail (protocol v5 <c>cover_render</c>, 1.29.0): decode the first frame, optionally crop one half
/// of a jacket spread, downscale to the thumbnail bound (Lanczos, never upscaled), write WebP, and hash the written file
/// exactly as <c>image_hash</c> hashes a stored thumbnail - so a rendered cover and a file thumbnail compare on equal terms.
/// Provider images (<paramref name="strictFormats"/>) decode only as JPEG, PNG, GIF or WebP after a magic-byte sniff, like
/// <see cref="ImageHasher"/>; archive pages decode like page extraction does. Dimension caps are checked before the pixels
/// are decoded.
/// </summary>
public static class CoverRenderer
{
    /// <summary>
    /// The columns of the cover inside a spread: <c>min(width / 2, round(height x aspect))</c> wide, aligned to the OUTER edge
    /// of the chosen half - left: from x = 0; right: ending at the right edge. Never empty.
    /// </summary>
    public static (int X, int Width) CropColumns(int width, int height, string side, double aspect)
    {
        if (aspect <= 0 || aspect > 1 || double.IsNaN(aspect))
            aspect = CoverRenderLimits.DefaultCropAspect;
        var cover = Math.Max(1, Math.Min(width / 2, (int)Math.Round(height * aspect, MidpointRounding.AwayFromZero)));
        return side == CoverCropSides.Right ? (width - cover, cover) : (0, cover);
    }

    /// <summary>True for the crop sides the protocol knows.</summary>
    public static bool IsKnownSide(string? side) => side is CoverCropSides.None or CoverCropSides.Left or CoverCropSides.Right;

    public static RenderedCover Render(byte[] bytes, bool strictFormats, CoverRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(request);
        if (!IsKnownSide(request.CropSide))
            return RenderedCover.Failed(CoverRenderErrors.InvalidRequest);
        var maxInput = request.MaxInputDimension > 0
            ? Math.Min(request.MaxInputDimension, CoverRenderLimits.MaxInputDimension)
            : CoverRenderLimits.MaxInputDimension;

        MagickReadSettings? settings = null;
        if (strictFormats)
        {
            if (ImageHasher.SniffFormat(bytes) is not { } sniffed)
                return RenderedCover.Failed(CoverRenderErrors.DecodeFailed);
            settings = new MagickReadSettings { Format = sniffed };
        }

        try
        {
            var probe = settings is null ? new MagickImageInfo(bytes) : new MagickImageInfo(bytes, settings);
            if (strictFormats && !ImageHasher.IsAllowed(probe.Format))
                return RenderedCover.Failed(CoverRenderErrors.DecodeFailed);
            if (probe.Width == 0 || probe.Height == 0)
                return RenderedCover.Failed(CoverRenderErrors.DecodeFailed);
            if (probe.Width > (uint)maxInput || probe.Height > (uint)maxInput)
                return RenderedCover.Failed(CoverRenderErrors.TooLarge);

            using var image = settings is null ? new MagickImage(bytes) : new MagickImage(bytes, settings);
            var (sourceWidth, sourceHeight) = ((int)image.Width, (int)image.Height);

            if (request.CropSide != CoverCropSides.None)
            {
                var (x, width) = CropColumns(sourceWidth, sourceHeight, request.CropSide, request.CropAspect);
                image.Crop(new MagickGeometry(x, 0, (uint)width, (uint)sourceHeight));
                image.ResetPage();
            }

            var bound = request.MaxDimension > 0 ? request.MaxDimension : CoverRenderLimits.DefaultMaxDimension;
            if (image.Width > (uint)bound || image.Height > (uint)bound)
            {
                image.FilterType = FilterType.Lanczos;
                image.Resize(new MagickGeometry((uint)bound, (uint)bound) { Greater = true });
            }

            image.Format = MagickFormat.WebP;
            image.Quality = (uint)Math.Clamp(request.WebpQuality, 1, 100);
            try
            {
                image.Write(request.OutputPath);
            }
            catch (MagickException)
            {
                return RenderedCover.Failed(CoverRenderErrors.EncodeFailed);
            }

            ulong? hash = null;
            if (request.ComputeHash)
            {
                var hashed = ImageHasher.Hash(File.ReadAllBytes(request.OutputPath), CoverRenderLimits.MaxInputDimension);
                if (hashed.Error is not null)
                    return RenderedCover.Failed(CoverRenderErrors.EncodeFailed);
                hash = hashed.Hash;
            }
            return new RenderedCover((int)image.Width, (int)image.Height, sourceWidth, sourceHeight, hash, null);
        }
        catch (MagickException)
        {
            return RenderedCover.Failed(CoverRenderErrors.DecodeFailed);
        }
    }
}
