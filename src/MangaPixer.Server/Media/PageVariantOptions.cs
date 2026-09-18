namespace com.lifepixer.mangapixer.Server.Media;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Media;

/// <summary>
/// Server-authoritative bucket ladder for downscaled page variants (1.19.0).
///
/// The reader used to fetch a full-resolution WebP transcode for every page,
/// regardless of how large it was actually displayed. A phone showing a 4000px
/// scan at 1080 CSS pixels paid for every one of those pixels, and the browser's
/// bilinear downscale is visibly worse than a Lanczos one on line art and
/// screentones. With this ladder the client asks for a display-sized page and the
/// server produces (and caches) a Lanczos-downscaled variant per bucket.
///
/// The ladder is server-authoritative on purpose: an unbounded <c>maxDim</c>
/// would let clients mint an unbounded number of distinct cache entries per page.
/// Requests snap UP to the smallest bucket that still covers the request, so a
/// handful of buckets serve every viewport.
///
/// Override via <c>MangaPixer:Media:PageVariants:*</c>.
/// </summary>
public sealed class PageVariantOptions
{
    /// <summary>
    /// Upper bound on ladder length. Every bucket is a separate cached encode of
    /// every page, so the ladder stays short by construction.
    /// </summary>
    public const int MaxLadderLength = 6;

    /// <summary>
    /// Longest-edge sizes (px), ascending. A request for <c>maxDim</c> is served
    /// by the smallest bucket &gt;= <c>maxDim</c>; anything above the largest
    /// bucket falls back to the full-size transcode. An empty ladder disables
    /// sized page variants entirely (every request serves the full variant).
    /// Default: 1080, 1440, 2160.
    /// </summary>
    public int[] MaxDimensions { get; set; } = [1080, 1440, 2160];

    /// <summary>
    /// WebP quality (1-100) for sized page variants. Default: 82, matching the
    /// full-size page transcode.
    /// </summary>
    public int WebpQuality { get; set; } = 82;

    /// <summary>
    /// Resampling filter used for sized page variants when the reader does not
    /// name one: <c>sharp</c>, <c>balanced</c> or <c>soft</c> (see
    /// <see cref="PageVariantFilters"/>). Default: <c>balanced</c> (Mitchell).
    ///
    /// This deliberately differs from 1.19.x, which was always Lanczos: the
    /// default reader mode picks a size automatically, so the default filter is
    /// what most pages are actually encoded with, and Mitchell is the safer
    /// choice on screentones. Readers who prefer the old look ask for
    /// <c>sharp</c> explicitly.
    /// </summary>
    public string DefaultFilter { get; set; } = PageVariantFilters.Balanced;

    /// <summary>
    /// Validates the ladder. Called at startup so a malformed configuration fails
    /// loudly instead of silently serving the wrong sizes.
    /// </summary>
    /// <exception cref="InvalidOperationException">The ladder is malformed.</exception>
    public void Validate()
    {
        var ladder = MaxDimensions ?? [];

        if (ladder.Length > MaxLadderLength)
            throw new InvalidOperationException(
                $"MangaPixer:Media:PageVariants:MaxDimensions must have at most {MaxLadderLength} entries (got {ladder.Length}).");

        for (var i = 0; i < ladder.Length; i++)
        {
            if (ladder[i] <= 0)
                throw new InvalidOperationException(
                    "MangaPixer:Media:PageVariants:MaxDimensions entries must all be positive.");
            if (i > 0 && ladder[i] <= ladder[i - 1])
                throw new InvalidOperationException(
                    "MangaPixer:Media:PageVariants:MaxDimensions must be strictly ascending and distinct.");
        }

        if (WebpQuality is < 1 or > 100)
            throw new InvalidOperationException(
                "MangaPixer:Media:PageVariants:WebpQuality must be between 1 and 100.");

        // Normalise in place so the rest of the process only ever sees the
        // canonical lower-case name (it reaches cache keys and a response
        // header, where "Balanced" and "balanced" must not diverge).
        if (!PageVariantFilters.TryNormalize(DefaultFilter, out var normalizedFilter))
            throw new InvalidOperationException(
                "MangaPixer:Media:PageVariants:DefaultFilter must be one of " +
                PageVariantFilters.Vocabulary + $" (got '{DefaultFilter}').");
        DefaultFilter = normalizedFilter;
    }

    /// <summary>
    /// Snaps a requested longest edge UP to the smallest bucket that covers it.
    /// Returns null when the request should be served by the full-size variant:
    /// either it was absent/non-positive, or it exceeds the largest bucket (at
    /// which point a "downscale" would be an upscale).
    /// </summary>
    public int? SelectBucket(int requestedMaxDimension)
    {
        if (requestedMaxDimension <= 0)
            return null;

        var ladder = MaxDimensions ?? [];
        foreach (var bucket in ladder)
        {
            if (bucket >= requestedMaxDimension)
                return bucket;
        }

        return null;
    }

    /// <summary>
    /// Worker/cache variant string for a (bucket, filter) pair, e.g.
    /// <c>webp@1440:balanced</c>. Distinct per pair so
    /// <see cref="CacheService.BuildCacheKey"/> keeps one cache entry per
    /// (page, bucket, filter) combination - two filters at the same size are
    /// genuinely different bytes. Pre-1.20.0 <c>webp@&lt;bucket&gt;</c> entries
    /// are simply never looked up again and age out of the cache LRU.
    /// </summary>
    /// <param name="filter">
    /// A <see cref="PageVariantFilters"/> name. Unknown or empty input falls
    /// back to <see cref="PageVariantFilters.Sharp"/> so the string is always
    /// well formed; callers validate before they get here.
    /// </param>
    public static string VariantName(int bucket, string? filter)
    {
        var name = PageVariantFilters.TryNormalize(filter, out var normalized)
            ? normalized
            : PageVariantFilters.Sharp;
        return "webp@" + bucket.ToString(CultureInfo.InvariantCulture) + ":" + name;
    }
}
