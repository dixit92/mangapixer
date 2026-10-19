namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 1.45.0 (lane R, MangaList's recommendation 2): MangaUpdates' daily releases list, read only to learn WHICH series got a release -
/// <c>GET /v1/releases/days?page=N&amp;include_metadata=true</c> (MangaUpdates' public OpenAPI document, operation <c>listReleasesByDay</c>;
/// <c>include_metadata</c> adds the series' id to each release). Only those fixed parameters are sent - nothing from the library. Like
/// every provider class it only translates HTTP/JSON; the caller goes through <see cref="MetadataGateway.DetailCallAsync{T}"/>. Only the
/// series ids and the release's chapter text are read; titles and groups are ignored and never stored.
/// </summary>
public sealed class MangaUpdatesReleasesFeed
{
    private const string ApiBase = "https://" + MetadataHttp.MangaUpdatesApiHost + "/v1/";

    /// <summary>
    /// Pages read per poll at most. A page holds several hundred releases (the review instance, 2026-10-19: 10 pages, 6727 releases, the
    /// list longer still), newest first, so three pages every 6 hours cover what came out since the last poll.
    /// </summary>
    public const int MaxPages = 3;

    private readonly IHttpClientFactory _httpFactory;

    public MangaUpdatesReleasesFeed(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    /// <summary>The address of one page: the page number and the fixed <c>include_metadata=true</c>, nothing else.</summary>
    public static string PageUrl(int page) =>
        ApiBase + "releases/days?page=" + Math.Clamp(page, 1, MaxPages).ToString(CultureInfo.InvariantCulture) + "&include_metadata=true";

    /// <summary>One page of the day's releases.</summary>
    public async Task<MangaUpdatesReleasesPage> PageAsync(int page, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(MetadataHttp.MangaUpdatesApiClient);
        using var response = await client.GetAsync(PageUrl(page), ct);
        MetadataHttp.EnsureSuccess(response);
        return Read(await MetadataHttp.ReadBoundedAsync(response, MetadataHttp.MaxJsonBytes, ct));
    }

    internal static MangaUpdatesReleasesPage Read(byte[] json)
    {
        MuReleasesResponse body;
        try
        {
            body = JsonSerializer.Deserialize<MuReleasesResponse>(json) ?? throw new MetadataResponseInvalidException("empty_body");
        }
        catch (JsonException)
        {
            throw new MetadataResponseInvalidException("malformed_json");
        }
        var releases = (body.Results ?? [])
            .Where(r => r.Metadata?.Series?.SeriesId is > 0)
            .Select(r => new MangaUpdatesRelease(r.Metadata!.Series!.SeriesId!.Value, ChapterOf(r.Record?.Chapter)))
            .ToList();
        return new MangaUpdatesReleasesPage(releases, body.Results?.Count ?? 0, body.TotalHits, body.PerPage);
    }

    /// <summary>
    /// The highest chapter number a release's chapter text states (<c>12</c>, <c>12.5</c>, <c>12-14</c> -> 14, <c>c.12</c>), or null when
    /// it states none (an extra, a volume-only release).
    /// </summary>
    public static double? ChapterOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
            return null;
        double? best = null;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\d+(?:\.\d+)?",
                     System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
        {
            if (double.TryParse(m.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) && n is >= 0 and < 100_000)
                best = best is { } b ? Math.Max(b, n) : n;
        }
        return best;
    }

    private sealed record MuReleasesResponse
    {
        [JsonPropertyName("total_hits")] public int? TotalHits { get; init; }
        [JsonPropertyName("per_page")] public int? PerPage { get; init; }
        [JsonPropertyName("results")] public List<MuReleaseResult>? Results { get; init; }
    }

    private sealed record MuReleaseResult
    {
        [JsonPropertyName("record")] public MuReleaseRecord? Record { get; init; }
        [JsonPropertyName("metadata")] public MuReleaseMetadata? Metadata { get; init; }
    }

    private sealed record MuReleaseRecord
    {
        [JsonPropertyName("chapter")] public string? Chapter { get; init; }
    }

    private sealed record MuReleaseMetadata
    {
        [JsonPropertyName("series")] public MuReleaseSeries? Series { get; init; }
    }

    private sealed record MuReleaseSeries
    {
        [JsonPropertyName("series_id")] public long? SeriesId { get; init; }
    }
}

/// <summary>One release of the day: the series' MangaUpdates id and the highest chapter it states (null = none).</summary>
public sealed record MangaUpdatesRelease(long SeriesId, double? Chapter);

/// <summary>One page of the releases list: the releases with a series id, how many results the page had, and the paging totals.</summary>
public sealed record MangaUpdatesReleasesPage(IReadOnlyList<MangaUpdatesRelease> Releases, int Results, int? TotalHits, int? PerPage)
{
    /// <summary>True when another page follows (by the totals; without them, while a page is full).</summary>
    public bool HasMore(int page) =>
        Results > 0 && (TotalHits is { } total && PerPage is > 0 and var per ? page * per < total : Results >= 25);
}
