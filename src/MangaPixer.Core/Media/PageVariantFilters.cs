namespace com.lifepixer.mangapixer.Core.Media;

/// <summary>
/// Public vocabulary for the resampling filter used when the server produces a
/// downscaled page variant (1.20.0).
///
/// Why this is a reader-visible choice: a Lanczos downscale is the sharpest
/// option and is what 1.19.x always used, but on screentoned manga a sharp
/// server downscale followed by a second, near-unity browser resample to the
/// painted CSS size makes the halftone dots beat against the sampling grid -
/// visible moire on a high-DPI tablet. A softer server kernel damps the
/// high-frequency energy before the browser ever touches it, at the cost of
/// some line-art crispness. Which trade is right depends on the artwork and
/// the screen, so it is exposed rather than guessed.
///
/// These names are the wire vocabulary shared by both contracts: the HTTP
/// <c>?filter=</c> query parameter, the variant/cache-key string
/// (<c>webp@1440:balanced</c>), the <c>X-MangaPixer-Variant</c> response header
/// and the worker's <c>ExtractRequest.ResizeFilter</c>. They live in Core so
/// the server and the worker agree on one definition; mapping a name to an
/// actual ImageMagick filter is the worker's job
/// (<c>ImageVariantEncoder</c>), because nothing outside the worker may
/// reference Magick.NET types.
/// </summary>
public static class PageVariantFilters
{
    /// <summary>Sharpest; ImageMagick Lanczos. The 1.19.x behaviour.</summary>
    public const string Sharp = "sharp";

    /// <summary>Middle ground; ImageMagick Mitchell. The default.</summary>
    public const string Balanced = "balanced";

    /// <summary>Softest; ImageMagick Box, i.e. area averaging on a downscale.</summary>
    public const string Soft = "soft";

    /// <summary>The whole vocabulary, in increasing softness.</summary>
    public static readonly string[] All = [Sharp, Balanced, Soft];

    /// <summary>
    /// Normalises a caller-supplied filter name. Input is trimmed and matched
    /// case-insensitively; the canonical form is always lower-case, because it
    /// ends up inside a cache key and two spellings must not mint two cache
    /// entries for identical bytes.
    /// </summary>
    /// <returns>True when <paramref name="value"/> names a known filter.</returns>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim().ToLowerInvariant();
        foreach (var known in All)
        {
            if (string.Equals(candidate, known, StringComparison.Ordinal))
            {
                normalized = known;
                return true;
            }
        }
        return false;
    }

    /// <summary>Comma-separated vocabulary for error messages and docs.</summary>
    public static string Vocabulary => string.Join(", ", All);
}
