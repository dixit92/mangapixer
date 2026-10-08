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

    /// <summary>
    /// 1.34.0: the node's OWN link is "Collection about" a series - a folder of works about it (fan works). The record is shown as
    /// context (no numbers); nodes below never get this state (the collection stops inheritance).
    /// </summary>
    CollectionAbout = 6,

    /// <summary>
    /// 1.37.0: the node's OWN link is "Artist folder" - the works of one creator (the folder's declared creator). No record; nodes
    /// below never get this state (the artist folder stops inheritance).
    /// </summary>
    ArtistFolder = 7,
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

    /// <summary>
    /// 1.34.0: "Collection about" the linked record - a folder of works about that series (fan works), not the series itself. Stops
    /// inheritance like Don't match, but automatic matching continues below it (its items are works of their own). Folders only.
    /// What every reader does with it: <see cref="SeriesLinkStates"/>.
    /// </summary>
    CollectionAbout = 4,

    /// <summary>
    /// 1.37.0: an artist's folder - the works of one creator, each a work of its own. Never linked to a record (RecordId null), stops
    /// inheritance (nothing inside inherits a link from it), and automatic matching continues inside it archive by archive, like
    /// <see cref="CollectionAbout"/>. The artist is the folder's declared creator. Folders only. What every reader does with it:
    /// <see cref="SeriesLinkStates"/>.
    /// </summary>
    ArtistFolder = 5,
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

    /// <summary>1.32.0: Italian-language comics (fumetti) - by language, like <see cref="French"/> (which covers Belgium).</summary>
    Italian = 14,

    /// <summary>1.32.0: Dutch-language comics (Netherlands and Flanders) - by language.</summary>
    Dutch = 15,
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
        // 1.32.0: the comics databases a ComicInfo Web link names (Metron, the Grand Comics Database) - shown, never called.
        "metron.cloud",
        "comics.org", "www.comics.org",
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
    /// the web" switch can be turned on. A change of the network surface bumps it; an
    /// instance that accepted an older version stops fetching until an admin accepts again.
    /// 2 (1.28.0): the text describes the provider allowlist (MangaUpdates + AniList).
    /// 3 (1.29.0): MangaDex (volume covers and volume lists) joins the allowed sites.
    /// 4 (1.32.0): the Grand Comics Database (comics) and Wikipedia (volume -> chapter lists) join the allowed sites;
    /// requests carry a fixed User-Agent naming MangaPixer, its version and its project URL.
    /// </summary>
    public const int CurrentVersion = 4;
}
