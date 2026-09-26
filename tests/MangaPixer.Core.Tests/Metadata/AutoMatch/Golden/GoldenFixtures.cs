namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch.Golden;

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>
/// The recorded MangaUpdates responses of the golden set (embedded
/// <c>Metadata/Fixtures/GoldenSet/*.json</c>, see the README there): automatic searches keyed by
/// (query text, doujinshi allowed) and series records keyed by id, mapped into
/// <see cref="MatchCandidate"/>s the way the server's provider maps them (title, alt titles, type,
/// start year, volume count and chapter total from the status line, latest chapter, authors, relations,
/// webtoon vote).
/// </summary>
internal static partial class GoldenFixtures
{
    private const string Prefix = "GoldenSet.";

    [GeneratedRegex(@"(\d{1,5})\s*(?:Volumes?|Vols?\.?)\b[^()\n]*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeLine();

    [GeneratedRegex(@"^\s*(\d{1,5})\s*Chapters?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterTotal();

    private static readonly Lazy<(Dictionary<(string, bool), SearchFixture> Searches, Dictionary<string, JsonElement> Series)> s_all = new(Load);

    internal sealed record SearchFixture(string Query, bool DoujinAllowed, IReadOnlyList<MatchCandidate> Hits);

    public static SearchFixture? Search(string query, bool doujinAllowed) =>
        s_all.Value.Searches.TryGetValue((query, doujinAllowed), out var f) ? f : null;

    public static MatchCandidate? Series(string externalId) =>
        s_all.Value.Series.TryGetValue(externalId, out var e) ? MapSeries(e) : null;

    public static int SearchCount => s_all.Value.Searches.Count;

    public static int SeriesCount => s_all.Value.Series.Count;

    private static (Dictionary<(string, bool), SearchFixture>, Dictionary<string, JsonElement>) Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        var searches = new Dictionary<(string, bool), SearchFixture>();
        var series = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement.Clone();
            if (name.StartsWith(Prefix + "search.", StringComparison.Ordinal))
            {
                var query = root.GetProperty("query").GetString()!;
                var doujin = !root.GetProperty("filter_types").EnumerateArray().Any(t => t.GetString() == "Doujinshi");
                var hits = root.GetProperty("response").GetProperty("results").EnumerateArray().Select(MapHit).ToList();
                searches[(query, doujin)] = new SearchFixture(query, doujin, hits);
            }
            else if (name.StartsWith(Prefix + "series.", StringComparison.Ordinal))
            {
                series[Id(root.GetProperty("series_id"))] = root;
            }
        }
        return (searches, series);
    }

    private static MatchCandidate MapHit(JsonElement result)
    {
        var record = result.GetProperty("record");
        var title = record.GetProperty("title").GetString()!;
        var hit = Str(result, "hit_title");
        var type = Str(record, "type");
        return new MatchCandidate(
            "mangaupdates",
            Id(record.GetProperty("series_id")),
            title,
            hit is not null && !string.Equals(hit, title, StringComparison.OrdinalIgnoreCase) ? [hit] : [],
            FormatOf(type),
            type,
            Year(Str(record, "year")),
            null,
            null,
            [],
            []);
    }

    private static MatchCandidate MapSeries(JsonElement s)
    {
        var type = Str(s, "type");
        var volumes = VolumeLine().Match(Str(s, "status") ?? string.Empty) is { Success: true } m
            && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : (int?)null;
        int? latest = s.TryGetProperty("latest_chapter", out var lc) && lc.ValueKind == JsonValueKind.Number && lc.GetDouble() > 0
            ? (int)lc.GetDouble() : null;
        int? totalChapters = ChapterTotal().Match(Str(s, "status") ?? string.Empty) is { Success: true } t
            && int.TryParse(t.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
        var authors = Array(s, "authors").Select(a => Str(a, "name")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var relations = Array(s, "related_series")
            .Where(r => r.TryGetProperty("related_series_id", out _))
            .Select(r => new CandidateRelation(Id(r.GetProperty("related_series_id")), Str(r, "relation_type") ?? string.Empty))
            .ToList();
        bool? webtoon = null;
        foreach (var c in Array(s, "categories"))
        {
            if (Str(c, "category") == "Webtoon/Webcomic")
                webtoon = (c.GetProperty("votes_plus").GetInt32() - c.GetProperty("votes_minus").GetInt32()) >= 5 ? true : null;
        }
        return new MatchCandidate(
            "mangaupdates",
            Id(s.GetProperty("series_id")),
            s.GetProperty("title").GetString()!,
            Array(s, "associated").Select(a => Str(a, "title")).OfType<string>().ToList(),
            FormatOf(type),
            type,
            Year(Str(s, "year")),
            volumes,
            latest,
            authors,
            relations,
            webtoon,
            totalChapters);
    }

    private static MetadataFormat? FormatOf(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "novel" => MetadataFormat.Novel,
        "artbook" => MetadataFormat.Artbook,
        "doujinshi" => MetadataFormat.Doujinshi,
        "drama cd" => MetadataFormat.Audio,
        _ => MetadataFormat.Comic,
    };

    private static int? Year(string? year) =>
        int.TryParse(year?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 1800 and <= 2200 ? y : null;

    private static string Id(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number ? e.GetInt64().ToString(CultureInfo.InvariantCulture) : e.GetString()!;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray() : [];
}
