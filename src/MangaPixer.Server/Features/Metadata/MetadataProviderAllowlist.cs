namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using System.Text.Json;
using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// The provider allowlist (1.28.0, owner decision d): ONE general metadata consent plus the list of approved sites
/// MangaPixer may contact, each removable by an admin and restorable. The approved sites are fixed in code (a new
/// site needs an AGENTS.md Privacy amendment and bumps <see cref="Core.Metadata.MetadataConsent.CurrentVersion"/>, so
/// an update that changes this list stops the fetcher until an admin accepts again). What an admin removed is kept
/// in <c>app_settings.MetadataProvidersJson</c> as <c>{"removed":["anilist"]}</c>; null = every approved site in.
/// The gateway refuses every request (manual and automatic) to a removed site. 1.32.0: the Grand Comics Database and
/// Wikipedia join (consent version 4).
/// </summary>
public static class MetadataProviderAllowlist
{
    public const int MaxJsonLength = 4096;

    public const string MangaUpdates = "mangaupdates";
    public const string AniList = "anilist";
    public const string MangaDex = "mangadex";

    /// <summary>The Grand Comics Database (1.32.0): the comics provider - Identify, automatic matching and refresh of comics.</summary>
    public const string Gcd = "gcd";

    /// <summary>Wikipedia + Wikidata (1.32.0): the volume -> chapter list of an already-linked series; never matches a folder.</summary>
    public const string Wikipedia = "wikipedia";

    /// <summary>An approved site: what it is used for and what is sent to it (shown on the chip).</summary>
    public sealed record ApprovedProvider(string Id, string Name, IReadOnlyList<string> Hosts, string UsedFor, string Sends);

    /// <summary>Every approved site, in display order.</summary>
    public static IReadOnlyList<ApprovedProvider> Approved { get; } =
    [
        new(MangaUpdates, "MangaUpdates", [MetadataHttp.MangaUpdatesApiHost, MetadataHttp.MangaUpdatesImageHost],
            "Series details, cover art, publication status and English release totals.",
            "The search text you confirm (usually a folder or file name) and MangaUpdates record numbers; with Automatic "
            + "matching on, also the cleaned names of new series folders."),
        new(Gcd, "Grand Comics Database", [MetadataHttp.GcdApiHost, MetadataHttp.GcdImageHost],
            "Comics and graphic novels: series details (publisher, start year, country, language, format, issue or volume "
            + "count); cover thumbnails to help you choose in Identify.",
            "The search text you confirm (usually a folder or file name), a start year when the name has one, and GCD record "
            + "numbers; with Automatic matching on, also the cleaned names of new comics folders."),
        new(MangaDex, "MangaDex", [MetadataHttp.MangaDexApiHost, MetadataHttp.MangaDexImageHost],
            "Volume covers, and which chapters make up each volume, for series already linked to MangaUpdates.",
            "The MangaUpdates title of a linked series (never a folder or file name), MangaDex record numbers and your cover "
            + "languages; downloads cover images."),
        new(AniList, "AniList", [MetadataHttp.AniListHost],
            "Chapters per volume, for the Missing report and virtual volumes - when an admin asks, or automatically when "
            + "MangaDex has no volume list.",
            "The AniList record number, or the MangaUpdates title of a series that is already linked (never a folder or file name)."),
        new(Wikipedia, "Wikipedia", [MetadataHttp.WikipediaHost, MetadataHttp.WikidataHost],
            "Which chapters make up each volume, with English release dates and ISBNs, for series already linked to "
            + "MangaUpdates - from the English \"List of ... chapters\" page.",
            "The MangaUpdates record number of a linked series (to Wikidata, to find its English article), the page titles "
            + "found that way, or the MangaUpdates title of the linked series (never a folder or file name)."),
    ];

    private sealed record State([property: JsonPropertyName("removed")] List<string>? Removed);

    private static readonly JsonSerializerOptions s_options = new() { PropertyNameCaseInsensitive = true };

    public static bool IsKnown(string? id) => id is not null && Approved.Any(p => p.Id == id);

    /// <summary>The ids an admin removed (unknown ids and a malformed column read as nothing removed).</summary>
    public static IReadOnlySet<string> Removed(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var state = JsonSerializer.Deserialize<State>(json, s_options);
            return (state?.Removed ?? []).Where(IsKnown).ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public static bool IsAllowed(string? json, string providerId) => IsKnown(providerId) && !Removed(json).Contains(providerId);

    /// <summary>The column value for a set of removed ids (null when nothing is removed).</summary>
    public static string? Write(IEnumerable<string> removed)
    {
        var list = removed.Where(IsKnown).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return list.Count == 0 ? null : JsonSerializer.Serialize(new State(list));
    }

    public static IReadOnlyList<MetadataProviderDto> ToDtos(string? json)
    {
        var removed = Removed(json);
        return Approved.Select(p => new MetadataProviderDto
        {
            Id = p.Id,
            Name = p.Name,
            Hosts = p.Hosts,
            UsedFor = p.UsedFor,
            Sends = p.Sends,
            Allowed = !removed.Contains(p.Id),
        }).ToList();
    }
}
