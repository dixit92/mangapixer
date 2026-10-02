namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.Gcd;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;

/// <summary>
/// The Grand Comics Database (<c>www.comics.org</c>, anonymous API) as an <see cref="IMetadataProvider"/> (1.32.0, lane B; owner-approved,
/// AGENTS.md Privacy). Like MangaUpdates it ONLY translates HTTP/JSON into records: switches, consent, budget, the 25-an-hour bucket,
/// backoff and logging live in <see cref="MetadataGateway"/>, the one caller.
///
/// Requests (all GET, paths under <c>/api/</c> only, the fixed <c>?format=json</c>):
/// <list type="bullet">
/// <item><c>/api/series/name/&lt;text&gt;/</c> or <c>/api/series/name/&lt;text&gt;/year/&lt;YYYY&gt;/</c> - the confirmed (or, automatic, the
/// cleaned) name and a start year taken from that name; a substring search, 50 series per page, each a WHOLE series record, so a
/// hit needs no further GET;</item>
/// <item><c>/api/series/&lt;id&gt;/</c> - by GCD id (refresh, a pasted <c>comics.org/series/&lt;id&gt;/</c>, a GCD id in ComicInfo);</item>
/// <item><c>/api/publisher/&lt;id&gt;/</c> - a publisher's name, remembered per id for the life of the process;</item>
/// <item><c>/api/issue/&lt;id&gt;/</c> - the first issue of a CHOSEN candidate only: cover thumbnail (identification), credits, pages.</item>
/// </list>
/// A Cloudflare challenge (an HTML page instead of JSON, with any status) is read as "blocked" and reported as a 403, which GCD's
/// transport treats as "slow down" - a backoff, like a 429.
/// </summary>
internal sealed class GcdProvider : IMetadataProvider
{
    public const string ProviderId = GcdMapping.ProviderId;
    public const string ProviderName = GcdMapping.ProviderName;
    private const string ApiBase = "https://" + MetadataHttp.GcdApiHost + "/api/";
    private const string Format = "?format=json";
    private const int MaxRememberedPublishers = 5000;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ConcurrentDictionary<long, string> _publishers = new();

    public GcdProvider(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public string Id => ProviderId;
    public string DisplayName => ProviderName;
    public MetadataProviderKind Kind => MetadataProviderKind.Online;
    public MetadataCapabilities Capabilities => MetadataCapabilities.SeriesSearch | MetadataCapabilities.SeriesGet | MetadataCapabilities.Covers;

    public bool TryParseReference(string input, out ProviderRef reference)
    {
        var (kind, id) = GcdReference.Parse(input);
        reference = new ProviderRef(ProviderId, kind == GcdReferenceKind.Series ? id.ToString(CultureInfo.InvariantCulture) : string.Empty);
        return kind == GcdReferenceKind.Series;
    }

    /// <summary>The name of a publisher when it was read before (no request), else null.</summary>
    public string? KnownPublisherName(long? publisherId) =>
        publisherId is { } id && _publishers.TryGetValue(id, out var name) ? name : null;

    /// <summary>The search path of a text (and a start year): <c>series/name/&lt;text&gt;/[year/&lt;YYYY&gt;/]</c>, or null when nothing can be searched.</summary>
    internal static string? SearchPath(string text, int? startYear, int page)
    {
        // A "/" would split the path, and a segment of dots would walk out of it: both become spaces.
        var cleaned = MetadataGateway.NormalizeQuery(text.Replace('/', ' ').Replace('\\', ' '));
        if (cleaned.Length == 0 || cleaned.All(c => c is '.' or ' '))
            return null;
        var path = "series/name/" + Uri.EscapeDataString(cleaned) + "/";
        if (startYear is >= 1800 and <= 2200)
            path += "year/" + startYear.Value.ToString(CultureInfo.InvariantCulture) + "/";
        return path + Format + (page > 1 ? "&page=" + page.ToString(CultureInfo.InvariantCulture) : string.Empty);
    }

    public async Task<ProviderSearchPage> SearchSeriesAsync(ProviderSearchQuery query, CancellationToken ct)
    {
        if (SearchPath(query.Text, query.StartYear, Math.Clamp(query.Page, 1, 100)) is not { } path)
            return new ProviderSearchPage([], 0);
        var body = await GetJsonAsync<GcdSeriesPage>(path, notFoundIsNull: true, ct);
        if (body is null)
            return new ProviderSearchPage([], 0);

        var hits = new List<ProviderSearchHit>();
        foreach (var series in GcdMapping.Distinct(body.Results ?? []))
        {
            var record = GcdMapping.ToRecord(series, 0, KnownPublisherName(GcdMapping.IdFromApiUrl(series.Publisher, "publisher")));
            hits.Add(new ProviderSearchHit
            {
                ExternalId = record.ExternalId,
                Title = record.Title,
                ProviderType = record.ProviderType,
                Year = record.StartYear,
                ImageRemoteUrl = null,
                Record = record,
            });
        }
        return new ProviderSearchPage(hits, Math.Max(body.Count ?? hits.Count, hits.Count));
    }

    public async Task<ProviderSeriesRecord?> GetSeriesAsync(string externalId, CancellationToken ct)
    {
        if (!TryId(externalId, out var id))
            return null;
        var series = await GetJsonAsync<GcdSeries>("series/" + id.ToString(CultureInfo.InvariantCulture) + "/" + Format, notFoundIsNull: true, ct);
        return series is null ? null : GcdMapping.ToRecord(series, id, KnownPublisherName(GcdMapping.IdFromApiUrl(series.Publisher, "publisher")));
    }

    /// <summary>A publisher's name (one request the first time, then remembered); null when GCD does not know the id.</summary>
    public async Task<string?> GetPublisherNameAsync(long publisherId, CancellationToken ct)
    {
        if (KnownPublisherName(publisherId) is { } known)
            return known;
        var publisher = await GetJsonAsync<GcdPublisher>("publisher/" + publisherId.ToString(CultureInfo.InvariantCulture) + "/" + Format, notFoundIsNull: true, ct);
        if (MetadataText.Line(publisher?.Name, 256) is not { } name)
            return null;
        if (_publishers.Count < MaxRememberedPublishers)
            _publishers[publisherId] = name;
        return name;
    }

    /// <summary>An issue's details (the chosen candidate's first issue); null when GCD does not know the id.</summary>
    public async Task<GcdIssueDetail?> GetIssueAsync(long issueId, CancellationToken ct)
    {
        var issue = await GetJsonAsync<GcdIssue>("issue/" + issueId.ToString(CultureInfo.InvariantCulture) + "/" + Format, notFoundIsNull: true, ct);
        return issue is null ? null : GcdMapping.ToIssueDetail(issue);
    }

    private async Task<T?> GetJsonAsync<T>(string relative, bool notFoundIsNull, CancellationToken ct) where T : class
    {
        var client = _httpFactory.CreateClient(MetadataHttp.GcdApiClient);
        using var response = await client.GetAsync(ApiBase + relative, HttpCompletionOption.ResponseHeadersRead, ct);
        if (IsChallenge(response))
            throw new MetadataHttpStatusException(HttpStatusCode.Forbidden, response.Headers.RetryAfter?.Delta, response.Headers.RetryAfter?.Date);
        if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
            return null;
        MetadataHttp.EnsureSuccess(response);
        var bytes = await MetadataHttp.ReadBoundedAsync(response, MetadataHttp.MaxJsonBytes, ct);
        // Cloudflare can also answer 200 with an HTML page: anything that is not a JSON object is a block.
        var first = bytes.SkipWhile(b => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or 0xEF or 0xBB or 0xBF).FirstOrDefault();
        if (first == (byte)'<')
            throw new MetadataHttpStatusException(HttpStatusCode.Forbidden, null, null);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes) ?? throw new MetadataResponseInvalidException("empty_body");
        }
        catch (JsonException)
        {
            throw new MetadataResponseInvalidException("malformed_json");
        }
    }

    /// <summary>An HTML answer (Cloudflare's challenge page or block page), whatever the status - never a GCD API answer.</summary>
    internal static bool IsChallenge(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType is { } type
        && type.Contains("html", StringComparison.OrdinalIgnoreCase);

    private static bool TryId(string? text, out long id)
    {
        id = 0;
        return text is { Length: > 0 and <= 10 } && text.All(char.IsAsciiDigit)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }
}
