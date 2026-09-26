namespace com.lifepixer.mangapixer.Core.Metadata;

using System.Text.RegularExpressions;

// Normalized series-metadata vocabulary (1.24.0). Stored as the enums' int
// values; serialized over the API as their names (JsonStringEnumConverter).
// Values are append-only: a stored int must keep its meaning.

/// <summary>What the series-info resolver found for a node.</summary>
public enum SeriesInfoState
{
    /// <summary>No series information (also returned when "Show series information" is off).</summary>
    None = 0,

    /// <summary>Only ComicInfo.xml data (own archive, or a folder whose items agree on one series).</summary>
    ComicInfo = 1,

    /// <summary>Only a linked web record.</summary>
    Web = 2,

    /// <summary>A linked web record plus ComicInfo, merged per field by precedence.</summary>
    WebAndComicInfo = 3,

    /// <summary>A folder whose items carry several ComicInfo series without a 60% majority.</summary>
    Mixed = 4,

    /// <summary>The nearest link is "Don't match" and no ComicInfo exists.</summary>
    DontMatch = 5,
}

/// <summary>Which source wins for series-level fields.</summary>
public enum MetadataPrecedence
{
    WebFirst = 0,
    ComicInfoFirst = 1,
}

/// <summary>Where the effective precedence came from.</summary>
public enum MetadataPrecedenceSource
{
    Default = 0,
    Library = 1,
    Folder = 2,
}

/// <summary>State of a node's series link (<c>node_series_links.State</c>).</summary>
public enum SeriesLinkState
{
    /// <summary>Admin-confirmed link to a record.</summary>
    Confirmed = 0,

    /// <summary>Reserved for stage 2 auto-match.</summary>
    Auto = 1,

    /// <summary>Reserved for stage 2 review queue.</summary>
    NeedsReview = 2,

    /// <summary>"This node is not one series": stops inheritance, never auto-matched.</summary>
    DontMatch = 3,
}

/// <summary>How a link was made (<c>node_series_links.MatchMethod</c>).</summary>
public enum MetadataMatchMethod
{
    Search = 0,
    Reference = 1,
    ComicInfoWebHint = 2,
    Auto = 3,
}

/// <summary>Whether a record came from an online API or a local dump (same key either way).</summary>
public enum MetadataSourceKind
{
    OnlineApi = 0,
    LocalDump = 1,
}

/// <summary>Normalized country / region / language of origin (MangaUpdates <c>type</c> maps here).</summary>
public enum MetadataOrigin
{
    Japan = 0,
    Korea = 1,
    ChinaTaiwan = 2,
    EnglishOriginal = 3,
    Philippines = 4,
    Indonesia = 5,
    Thailand = 6,
    Vietnam = 7,
    Malaysia = 8,
    Nordic = 9,
    French = 10,
    Spanish = 11,
    German = 12,
    Other = 13,
}

/// <summary>Publication format, independent of origin.</summary>
public enum MetadataFormat
{
    Comic = 0,
    Novel = 1,
    Artbook = 2,
    Doujinshi = 3,
    Audio = 4,
}

/// <summary>Publication status in the country of origin.</summary>
public enum MetadataOriginStatus
{
    Unknown = 0,
    Ongoing = 1,
    Complete = 2,
    Hiatus = 3,
    Cancelled = 4,
}

/// <summary>Attribution of one resolved field.</summary>
public enum MetadataFieldSource
{
    Web = 0,
    ComicInfo = 1,
}

/// <summary>Validation helpers shared by the admin endpoints and (later) the providers.</summary>
public static partial class MetadataIdentifiers
{
    /// <summary>Provider ids are short lowercase slugs: <c>mangaupdates</c>, <c>anilist</c>, ...</summary>
    public const int MaxProviderLength = 32;

    /// <summary>Provider-side record ids are stored as text.</summary>
    public const int MaxExternalIdLength = 64;

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderPattern();

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExternalIdPattern();

    public static bool IsValidProvider(string? provider) =>
        provider is not null && ProviderPattern().IsMatch(provider);

    public static bool IsValidExternalId(string? externalId) =>
        externalId is not null && ExternalIdPattern().IsMatch(externalId);
}

/// <summary>
/// Hosts whose URLs from ComicInfo <c>Web</c> may be shown as links (a user
/// navigation, never a call made by MangaPixer). Anything else is dropped.
/// </summary>
public static class MetadataWebLinks
{
    private static readonly HashSet<string> s_allowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mangaupdates.com", "www.mangaupdates.com",
        "anilist.co",
        "mangadex.org",
        "myanimelist.net",
        "comicvine.gamespot.com",
        "kitsu.app", "kitsu.io",
        "mangabaka.dev",
    };

    /// <summary>True for an absolute http(s) URL on an allowlisted host (older taggers wrote http).</summary>
    public static bool IsAllowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.IsDefaultPort
        && s_allowedHosts.Contains(uri.Host);
}

/// <summary>Consent versions for the web-metadata switch (the text lives in the web client, lane B2).</summary>
public static class MetadataConsent
{
    /// <summary>
    /// The consent text version an admin must accept before the global "Fetch from
    /// the web" switch can be turned on. A change of the network surface bumps it.
    /// </summary>
    public const int CurrentVersion = 1;
}
