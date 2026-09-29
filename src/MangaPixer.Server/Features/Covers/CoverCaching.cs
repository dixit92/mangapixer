namespace com.lifepixer.mangapixer.Server.Features.Covers;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Cache headers of the versioned cover URLs (1.29.0, owner Q14): covers are authenticated content, so they are never
/// <c>public</c>. A request whose <c>v</c> equals the CURRENT token gets the image cached for a year (the URL changes
/// whenever the image does); any other request - an old token, or no token (old clients, bookmarks) - still gets the
/// current image, but only with <c>no-cache</c> + an ETag, so a stale URL can never pin old bytes.
/// </summary>
internal static class CoverCaching
{
    public const string Immutable = "private, max-age=31536000, immutable";
    public const string Revalidate = "private, no-cache";

    /// <summary>
    /// Sets Cache-Control and ETag; returns true when the client's <c>If-None-Match</c> already names this image (answer 304).
    /// </summary>
    public static bool Apply(HttpResponse response, HttpRequest request, string etagValue, bool current)
    {
        var etag = $"\"{etagValue}\"";
        response.Headers.CacheControl = current ? Immutable : Revalidate;
        response.Headers.ETag = etag;
        foreach (var candidate in request.Headers.IfNoneMatch)
        {
            if (candidate is null)
                continue;
            foreach (var part in candidate.Split(','))
            {
                var tag = part.Trim();
                if (tag.StartsWith("W/", StringComparison.Ordinal))
                    tag = tag[2..];
                if (tag == etag || tag == "*")
                    return true;
            }
        }
        return false;
    }
}
