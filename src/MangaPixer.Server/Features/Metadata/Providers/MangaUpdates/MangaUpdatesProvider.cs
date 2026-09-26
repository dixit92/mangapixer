namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// MangaUpdates (API v1) as an <see cref="IMetadataProvider"/> (1.24.0, lane B2).
/// It ONLY translates HTTP/JSON into provider records: every policy (switches,
/// consent, budget, rate limit, backoff, logging) lives in
/// <see cref="MetadataGateway"/>, the one caller. Internal on purpose: nothing
/// outside this assembly can reach it except through DI, and inside it only the
/// gateway resolves providers.
///
/// Requests: <c>POST /v1/series/search</c> with exactly
/// <c>{"search", "page", "perpage"}</c> (plus the FIXED <c>"filter_types"</c> list
/// <see cref="HiddenTypes"/> when the admin hides doujinshi and novels), and
/// <c>GET /v1/series/{id}</c> with the numeric id only. Nothing else is sent
/// (headers come from the named client).
/// </summary>
internal sealed class MangaUpdatesProvider : IMetadataProvider
{
    public const string ProviderId = "mangaupdates";
    public const string ProviderName = "MangaUpdates";
    private const string ApiBase = "https://" + MetadataHttp.MangaUpdatesApiHost + "/v1/";

    /// <summary>Provider types left out by "Hide doujinshi &amp; novels" (a fixed list, never user data).</summary>
    internal static readonly IReadOnlyList<string> HiddenTypes = ["Doujinshi", "Novel", "Artbook", "Drama CD"];

    private readonly IHttpClientFactory _httpFactory;

    public MangaUpdatesProvider(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public string Id => ProviderId;
    public string DisplayName => ProviderName;
    public MetadataProviderKind Kind => MetadataProviderKind.Online;
    public MetadataCapabilities Capabilities => MetadataCapabilities.SeriesSearch | MetadataCapabilities.SeriesGet | MetadataCapabilities.Covers;

    public bool TryParseReference(string input, out ProviderRef reference)
    {
        var (kind, id) = MangaUpdatesReference.Parse(input);
        reference = new ProviderRef(ProviderId, kind == MangaUpdatesReferenceKind.Series ? id.ToString(CultureInfo.InvariantCulture) : string.Empty);
        return kind == MangaUpdatesReferenceKind.Series;
    }

    public async Task<ProviderSearchPage> SearchSeriesAsync(ProviderSearchQuery query, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(MetadataHttp.MangaUpdatesApiClient);
        using var content = JsonContent.Create(new MuSearchRequest(
            query.Text, query.Page, query.PerPage, query.HideDoujinshiAndNovels ? HiddenTypes : null));
        using var response = await client.PostAsync(ApiBase + "series/search", content, ct);
        MetadataHttp.EnsureSuccess(response);
        var body = Deserialize<MuSearchResponse>(await MetadataHttp.ReadBoundedAsync(response, MetadataHttp.MaxJsonBytes, ct));

        var hits = new List<ProviderSearchHit>();
        foreach (var result in body.Results ?? [])
        {
            var record = result.Record;
            if (record?.SeriesId is not { } id || id <= 0)
                continue;
            var title = MetadataText.Line(record.Title, 512);
            if (title is null)
                continue;
            var hitTitle = MetadataText.Line(result.HitTitle, 512);
            hits.Add(new ProviderSearchHit
            {
                ExternalId = id.ToString(CultureInfo.InvariantCulture),
                Title = title,
                HitTitle = hitTitle is not null && !string.Equals(hitTitle, title, StringComparison.OrdinalIgnoreCase) ? hitTitle : null,
                ProviderType = MetadataText.Line(record.Type, 32),
                Year = ParseYear(record.Year),
                ImageRemoteUrl = ImageUrl(record.Image?.Url?.Thumb) ?? ImageUrl(record.Image?.Url?.Original),
            });
        }
        return new ProviderSearchPage(hits, Math.Max(body.TotalHits ?? hits.Count, hits.Count));
    }

    public async Task<ProviderSeriesRecord?> GetSeriesAsync(string externalId, CancellationToken ct)
    {
        if (!long.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return null;

        var client = _httpFactory.CreateClient(MetadataHttp.MangaUpdatesApiClient);
        using var response = await client.GetAsync(ApiBase + "series/" + id.ToString(CultureInfo.InvariantCulture), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        MetadataHttp.EnsureSuccess(response);
        var series = Deserialize<MuSeries>(await MetadataHttp.ReadBoundedAsync(response, MetadataHttp.MaxJsonBytes, ct));
        return MangaUpdatesMapping.ToRecord(series, id);
    }

    private static T Deserialize<T>(byte[] json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json) ?? throw new MetadataResponseInvalidException("empty_body");
        }
        catch (JsonException)
        {
            throw new MetadataResponseInvalidException("malformed_json");
        }
    }

    internal static int? ParseYear(string? year) =>
        int.TryParse(year?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 1800 and <= 2200 ? y : null;

    /// <summary>An image URL is kept only when it is HTTPS on the image CDN (re-validated again before any fetch).</summary>
    internal static string? ImageUrl(string? url) =>
        url is { Length: <= 512 } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && HostAllowlistHandler.IsAllowed(uri, MangaUpdatesMapping.ImageHosts)
            ? uri.AbsoluteUri
            : null;
}

/// <summary>MangaUpdates -> normalized vocabulary (approved addendum on origin / format / webtoon).</summary>
public static class MangaUpdatesMapping
{
    /// <summary>The category MangaUpdates users vote on for webtoon-format series.</summary>
    public const string WebtoonCategory = "Webtoon/Webcomic";

    /// <summary>
    /// Net votes (plus minus minus) the webtoon category needs to count as "yes".
    /// Categories are crowd tags: one or two votes are often a single user's
    /// guess, while real webtoons collect dozens (Solo Leveling: 64). Five
    /// agreeing voters filters the noise without missing established series;
    /// below that the flag stays unknown rather than "no".
    /// </summary>
    public const int MinWebtoonVotes = 5;

    /// <summary>At most this many categories are stored (never displayed in stage 1).</summary>
    public const int MaxStoredCategories = 20;

    internal static readonly IReadOnlySet<string> ImageHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MetadataHttp.MangaUpdatesImageHost };

    private static readonly IReadOnlySet<string> s_siteHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "www.mangaupdates.com", "mangaupdates.com" };

    public static MetadataOrigin? OriginOf(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "manga" => MetadataOrigin.Japan,
        "manhwa" => MetadataOrigin.Korea,
        "manhua" => MetadataOrigin.ChinaTaiwan,
        "oel" => MetadataOrigin.EnglishOriginal,
        "filipino" => MetadataOrigin.Philippines,
        "indonesian" => MetadataOrigin.Indonesia,
        "thai" => MetadataOrigin.Thailand,
        "vietnamese" => MetadataOrigin.Vietnam,
        "malaysian" => MetadataOrigin.Malaysia,
        "nordic" => MetadataOrigin.Nordic,
        "french" => MetadataOrigin.French,
        "spanish" => MetadataOrigin.Spanish,
        "german" => MetadataOrigin.German,
        // Format values carry no origin.
        "novel" or "artbook" or "doujinshi" or "drama cd" => null,
        _ => MetadataOrigin.Other,
    };

    public static MetadataFormat? FormatOf(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "novel" => MetadataFormat.Novel,
        "artbook" => MetadataFormat.Artbook,
        "doujinshi" => MetadataFormat.Doujinshi,
        "drama cd" => MetadataFormat.Audio,
        _ => MetadataFormat.Comic,
    };

    /// <summary>
    /// Tri-state webtoon flag: null when the record carries no categories (unknown),
    /// or the webtoon category has fewer than <see cref="MinWebtoonVotes"/> net votes;
    /// true at or above it; false when categories exist but none is the webtoon one.
    /// </summary>
    internal static bool? WebtoonOf(IReadOnlyList<MuCategory>? categories)
    {
        if (categories is null || categories.Count == 0)
            return null;
        var webtoon = categories.FirstOrDefault(c => string.Equals(c.Category?.Trim(), WebtoonCategory, StringComparison.OrdinalIgnoreCase));
        if (webtoon is null)
            return false;
        return NetVotes(webtoon) >= MinWebtoonVotes ? true : null;
    }

    private static int NetVotes(MuCategory c) =>
        c.VotesPlus is { } plus && c.VotesMinus is { } minus ? plus - minus : c.Votes ?? 0;

    internal static ProviderSeriesRecord ToRecord(MuSeries s, long requestedId)
    {
        var id = s.SeriesId is > 0 ? s.SeriesId.Value : requestedId;
        var title = MetadataText.Line(s.Title, 512) ?? throw new MetadataResponseInvalidException("missing_title");
        var status = MangaUpdatesStatusParser.Parse(s.Status);

        var alt = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { title };
        foreach (var a in s.Associated ?? [])
        {
            if (alt.Count >= 100) break;
            if (MetadataText.Line(a.Title, 512) is { } t && seen.Add(t))
                alt.Add(t);
        }

        var creators = new List<MetadataJson.Creator>();
        var creatorKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var author in s.Authors ?? [])
        {
            if (creators.Count >= 50) break;
            if (MetadataText.Line(author.Name, 256) is not { } name) continue;
            var role = author.Type?.Trim().ToLowerInvariant() switch
            {
                "author" => "author",
                "artist" => "artist",
                _ => "other",
            };
            if (creatorKeys.Add(role + "\n" + name))
                creators.Add(new MetadataJson.Creator(name, role));
        }

        var publishers = new List<MetadataJson.Publisher>();
        foreach (var p in s.Publishers ?? [])
        {
            if (publishers.Count >= 30) break;
            if (MetadataText.Line(p.PublisherName, 256) is not { } name) continue;
            var kind = p.Type?.Trim().ToLowerInvariant() switch
            {
                "original" => "original",
                "english" => "english",
                _ => "other",
            };
            publishers.Add(new MetadataJson.Publisher(name, kind));
        }

        var genres = (s.Genres ?? [])
            .Select(g => MetadataText.Line(g.Genre, 64))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        var categories = (s.Categories ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.Category))
            .OrderByDescending(c => c.Votes ?? 0)
            .ThenBy(c => c.Category, StringComparer.Ordinal)
            .Take(MaxStoredCategories)
            .Select(c => new MetadataJson.Category(MetadataText.Line(c.Category, 128)!, c.Votes ?? 0))
            .ToList();

        var type = MetadataText.Line(s.Type, 32);
        return new ProviderSeriesRecord
        {
            Provider = MangaUpdatesProvider.ProviderId,
            ExternalId = id.ToString(CultureInfo.InvariantCulture),
            SourceKind = MetadataSourceKind.OnlineApi,
            Title = title,
            AltTitles = alt,
            Description = MetadataText.Flatten(s.Description, 16 * 1024),
            Origin = OriginOf(type),
            Format = FormatOf(type),
            Webtoon = WebtoonOf(s.Categories),
            ProviderType = type,
            StartYear = MangaUpdatesProvider.ParseYear(s.Year),
            OriginStatus = status.Status,
            OriginVolumes = status.Volumes,
            LatestChapter = s.LatestChapter is > 0 ? s.LatestChapter : null,
            StatusText = status.Text,
            LicensedEn = s.Licensed,
            TranslationComplete = s.Completed,
            Creators = creators,
            Genres = genres,
            Categories = categories,
            Publishers = publishers,
            SiteUrl = SiteUrl(s.Url),
            ImageRemoteUrl = MangaUpdatesProvider.ImageUrl(s.Image?.Url?.Original),
            ProviderUpdatedAt = s.LastUpdated?.Timestamp is > 0 and < 253402300800
                ? DateTimeOffset.FromUnixTimeSeconds(s.LastUpdated.Timestamp.Value)
                : null,
        };
    }

    private static string? SiteUrl(string? url) =>
        url is { Length: <= 512 } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && s_siteHosts.Contains(uri.Host)
            ? uri.AbsoluteUri
            : null;
}
