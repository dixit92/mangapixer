namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;

/// <summary>Outcome of parsing a pasted MangaUpdates reference.</summary>
public enum MangaUpdatesReferenceKind
{
    /// <summary>Not a MangaUpdates reference at all.</summary>
    Invalid = 0,

    /// <summary>A series id was recovered locally.</summary>
    Series = 1,

    /// <summary>A legacy <c>series.html?id=N</c> URL: its old ids do not map to current ones.</summary>
    Legacy = 2,
}

/// <summary>
/// Parses what an admin pastes into the identify dialog - <c>mu:51239621230</c>,
/// <c>mu:njeqwry</c> or a current series URL
/// <c>https://www.mangaupdates.com/series/njeqwry/berserk</c> - into the numeric
/// series id, LOCALLY: the website slug is the base36 form of <c>series_id</c>, so
/// match-by-URL never sends a title anywhere (only the id, on the following GET).
/// Legacy <c>series.html?id=</c> URLs carry old ids that do not map and are
/// reported as such.
/// </summary>
public static class MangaUpdatesReference
{
    /// <summary>A positive long is at most 13 base36 digits; overflow is checked while decoding.</summary>
    private const int MaxBase36Length = 13;

    private const string Shortcode = "mu:";

    private static readonly HashSet<string> s_siteHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "www.mangaupdates.com", "mangaupdates.com",
    };

    public static (MangaUpdatesReferenceKind Kind, long SeriesId) Parse(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 512)
            return (MangaUpdatesReferenceKind.Invalid, 0);

        if (text.StartsWith(Shortcode, StringComparison.OrdinalIgnoreCase))
        {
            var token = text[Shortcode.Length..].Trim();
            // All digits = the decimal id; otherwise the base36 slug form.
            if (token.Length > 0 && token.All(char.IsAsciiDigit))
                return long.TryParse(token, out var id) && id > 0
                    ? (MangaUpdatesReferenceKind.Series, id)
                    : (MangaUpdatesReferenceKind.Invalid, 0);
            return TryDecodeBase36(token, out var decoded)
                ? (MangaUpdatesReferenceKind.Series, decoded)
                : (MangaUpdatesReferenceKind.Invalid, 0);
        }

        // Accept a URL without a scheme too ("www.mangaupdates.com/series/...").
        var candidate = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !s_siteHosts.Contains(uri.Host))
            return (MangaUpdatesReferenceKind.Invalid, 0);

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 1 && segments[0].Equals("series.html", StringComparison.OrdinalIgnoreCase))
            return (MangaUpdatesReferenceKind.Legacy, 0);
        if (segments.Length >= 2 && segments[0].Equals("series", StringComparison.OrdinalIgnoreCase)
            && TryDecodeBase36(segments[1], out var fromUrl))
            return (MangaUpdatesReferenceKind.Series, fromUrl);
        return (MangaUpdatesReferenceKind.Invalid, 0);
    }

    /// <summary>Decodes a lowercase-or-uppercase base36 token (0-9a-z) into a positive id.</summary>
    public static bool TryDecodeBase36(string token, out long value)
    {
        value = 0;
        if (token.Length == 0 || token.Length > MaxBase36Length)
            return false;
        foreach (var raw in token)
        {
            var c = char.ToLowerInvariant(raw);
            int digit;
            if (c is >= '0' and <= '9') digit = c - '0';
            else if (c is >= 'a' and <= 'z') digit = c - 'a' + 10;
            else return false;
            if (value > (long.MaxValue - digit) / 36)
                return false;
            value = value * 36 + digit;
        }
        return value > 0;
    }

    /// <summary>The base36 slug form of a series id (the website URL segment).</summary>
    public static string ToBase36(long value)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        Span<char> buffer = stackalloc char[16];
        var pos = buffer.Length;
        while (value > 0)
        {
            var digit = (int)(value % 36);
            buffer[--pos] = (char)(digit < 10 ? '0' + digit : 'a' + digit - 10);
            value /= 36;
        }
        return new string(buffer[pos..]);
    }
}
