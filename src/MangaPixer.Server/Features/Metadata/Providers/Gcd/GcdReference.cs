namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.Gcd;

using System.Globalization;

/// <summary>Outcome of parsing a pasted Grand Comics Database reference.</summary>
public enum GcdReferenceKind
{
    Invalid = 0,

    /// <summary>A series id: <c>comics.org/series/&lt;id&gt;/</c> or <c>gcd:&lt;id&gt;</c>.</summary>
    Series = 1,

    /// <summary>An issue address (<c>comics.org/issue/&lt;id&gt;/</c>): its series is not known without a request, so it is refused.</summary>
    Issue = 2,
}

/// <summary>
/// Parses what an admin pastes into Identify, or what a ComicInfo <c>Web</c> field holds, into a GCD series id LOCALLY (1.32.0):
/// <c>https://www.comics.org/series/4347/</c> (any sub-page, with or without <c>www.</c> or a scheme, the API form
/// <c>/api/series/4347/</c> too) or the shortcode <c>gcd:4347</c>. Only the id is ever sent, on the following GET.
/// </summary>
public static class GcdReference
{
    private const string Shortcode = "gcd:";
    private const int MaxDigits = 10;

    private static readonly HashSet<string> s_siteHosts = new(StringComparer.OrdinalIgnoreCase) { "www.comics.org", "comics.org" };

    public static (GcdReferenceKind Kind, long SeriesId) Parse(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 512)
            return (GcdReferenceKind.Invalid, 0);

        if (text.StartsWith(Shortcode, StringComparison.OrdinalIgnoreCase))
            return TryId(text[Shortcode.Length..].Trim(), out var id) ? (GcdReferenceKind.Series, id) : (GcdReferenceKind.Invalid, 0);

        var candidate = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !s_siteHosts.Contains(uri.Host))
            return (GcdReferenceKind.Invalid, 0);

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 0 && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase))
            segments.RemoveAt(0);
        if (segments.Count >= 2 && TryId(segments[1], out var fromUrl))
        {
            if (segments[0].Equals("series", StringComparison.OrdinalIgnoreCase))
                return (GcdReferenceKind.Series, fromUrl);
            if (segments[0].Equals("issue", StringComparison.OrdinalIgnoreCase))
                return (GcdReferenceKind.Issue, 0);
        }
        return (GcdReferenceKind.Invalid, 0);
    }

    /// <summary>The public series page of a GCD series id (attribution and the "open on GCD" link).</summary>
    public static string SiteUrl(long seriesId) =>
        "https://" + MetadataHttp.GcdApiHost + "/series/" + seriesId.ToString(CultureInfo.InvariantCulture) + "/";

    private static bool TryId(string token, out long id)
    {
        id = 0;
        return token.Length is > 0 and <= MaxDigits && token.All(char.IsAsciiDigit)
            && long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }
}
