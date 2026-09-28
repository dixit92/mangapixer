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
/// The gateway refuses every request (manual and automatic) to a removed site.
/// </summary>
public static class MetadataProviderAllowlist
{
    public const int MaxJsonLength = 4096;

    public const string MangaUpdates = "mangaupdates";
    public const string AniList = "anilist";

    /// <summary>An approved site: what it is used for and what is sent to it (shown on the chip).</summary>
    public sealed record ApprovedProvider(string Id, string Name, IReadOnlyList<string> Hosts, string UsedFor, string Sends);

    /// <summary>Every approved site, in display order.</summary>
    public static IReadOnlyList<ApprovedProvider> Approved { get; } =
    [
        new(MangaUpdates, "MangaUpdates", [MetadataHttp.MangaUpdatesApiHost, MetadataHttp.MangaUpdatesImageHost],
            "Series details, cover art, publication status and English release totals.",
            "The search text you confirm (usually a folder or file name) and MangaUpdates record numbers; with Automatic "
            + "matching on, also the cleaned names of new series folders."),
        new(AniList, "AniList", [MetadataHttp.AniListHost],
            "Chapters per volume for the Missing report, when an admin asks for it.",
            "The MangaUpdates title of a series that is already linked (never a folder or file name)."),
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
