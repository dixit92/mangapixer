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
/// <c>GET /v1/series/{id}</c> with the numeric id only, and (1.38.0, artists' other names, only when an admin asks)
/// <c>GET /v1/authors/{author_id}</c> with the numeric author id a stored record lists for a creator. Nothing else is sent
/// (headers come from the named client).
/// </summary>
internal sealed class MangaUpdatesProvider : IMetadataProvider
{
    public const string ProviderId = "mangaupdates";
    public const string ProviderName = "MangaUpdates";
    private const string ApiBase = "https://" + MetadataHttp.MangaUpdatesApiHost + "/v1/";

    /// <summary>Provider types left out by "Hide doujinshi &amp; novels" (a fixed list, never user data).</summary>
    internal static readonly IReadOnlyList<string> HiddenTypes = ["Doujinshi", "Novel", "Artbook", "Drama CD"];

    /// <summary>The same fixed filter with doujinshi allowed (automatic searches below a doujinshi Content folder).</summary>
    internal static readonly IReadOnlyList<string> HiddenTypesAllowingDoujinshi = ["Novel", "Artbook", "Drama CD"];

    internal static IReadOnlyList<string>? FilterTypesOf(ProviderSearchQuery query) =>
        !query.HideDoujinshiAndNovels ? null : query.AllowDoujinshi ? HiddenTypesAllowingDoujinshi : HiddenTypes;

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
            query.Text, query.Page, query.PerPage, FilterTypesOf(query)));
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

    /// <summary>
    /// One author record (1.38.0): <c>GET /v1/authors/{id}</c> with the numeric id only; null when MangaUpdates does not know the
    /// id (404, an empty body). Called only through <see cref="MetadataGateway.DetailCallAsync{T}"/>.
    /// </summary>
    internal async Task<ProviderAuthorRecord?> GetAuthorAsync(long authorId, CancellationToken ct)
    {
        if (authorId <= 0)
            return null;

        var client = _httpFactory.CreateClient(MetadataHttp.MangaUpdatesApiClient);
        using var response = await client.GetAsync(ApiBase + "authors/" + authorId.ToString(CultureInfo.InvariantCulture), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        MetadataHttp.EnsureSuccess(response);
        var author = Deserialize<MuAuthorRecord>(await MetadataHttp.ReadBoundedAsync(response, MetadataHttp.MaxJsonBytes, ct));
        return MangaUpdatesMapping.ToAuthor(author, authorId);
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

    /// <summary>The image hosts of MangaUpdates: the single source is its gateway transport (1.29.0).</summary>
    internal static IReadOnlySet<string> ImageHosts => MetadataHttp.Transports[MetadataProviderAllowlist.MangaUpdates].ImageHosts;

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
                creators.Add(new MetadataJson.Creator(name, role,
                    author.AuthorId is > 0 ? author.AuthorId.Value.ToString(CultureInfo.InvariantCulture) : null));
        }

        var publishers = new List<MetadataJson.Publisher>();
        int? englishVolumes = null, englishChapters = null;
        foreach (var p in s.Publishers ?? [])
        {
            var edition = MangaUpdatesStatusParser.PublisherEdition.Empty;
            if (string.Equals(p.Type?.Trim(), "English", StringComparison.OrdinalIgnoreCase))
            {
                // 1.30.0: the regular edition only (an omnibus count is not the original's numbering), with its own status.
                edition = MangaUpdatesStatusParser.ParsePublisherEdition(p.Notes);
                englishVolumes = Max(englishVolumes, edition.Volumes);
                englishChapters = Max(englishChapters, edition.Chapters);
            }
            if (publishers.Count >= 30) break;
            if (MetadataText.Line(p.PublisherName, 256) is not { } name) continue;
            var kind = p.Type?.Trim().ToLowerInvariant() switch
            {
                "original" => "original",
                "english" => "english",
                _ => "other",
            };
            // 1.28.0: the English totals are stored with the publisher (the missing volumes / chapters report).
            publishers.Add(new MetadataJson.Publisher(name, kind, edition.Volumes, edition.Chapters,
                MetadataJson.Publisher.StatusWord(edition.Status), edition.Omnibus ? true : null));
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

        var publications = new List<MetadataJson.Publication>();
        foreach (var p in s.Publications ?? [])
        {
            if (publications.Count >= 30) break;
            if (MetadataText.Line(p.PublicationName, 256) is not { } name) continue;
            publications.Add(new MetadataJson.Publication(name, MetadataText.Line(p.PublisherName, 256)));
        }

        var relations = new List<MetadataJson.RelatedRecord>();
        foreach (var r in s.RelatedSeries ?? [])
        {
            if (relations.Count >= 50) break;
            if (r.RelatedSeriesId is not > 0) continue;
            relations.Add(new MetadataJson.RelatedRecord(
                r.RelatedSeriesId.Value.ToString(CultureInfo.InvariantCulture),
                MetadataText.Line(r.RelationType, 64)?.ToLowerInvariant() ?? "related"));
        }

        var type = MetadataText.Line(s.Type, 32);
        return new ProviderSeriesRecord
        {
            Provider = MangaUpdatesProvider.ProviderId,
            ExternalId = id.ToString(CultureInfo.InvariantCulture),
            SourceKind = MetadataSourceKind.OnlineApi,
            Title = title,
            AltTitles = alt,
            Description = MetadataText.Description(s.Description, 16 * 1024),
            Origin = OriginOf(type),
            Format = FormatOf(type),
            Webtoon = WebtoonOf(s.Categories),
            ProviderType = type,
            StartYear = MangaUpdatesProvider.ParseYear(s.Year),
            OriginStatus = status.Status,
            OriginVolumes = status.Volumes,
            LatestChapter = s.LatestChapter is > 0 ? s.LatestChapter : null,
            TotalChapters = status.Chapters,
            EnglishVolumes = englishVolumes,
            EnglishChapters = englishChapters,
            StatusText = status.Text,
            LicensedEn = s.Licensed,
            TranslationComplete = s.Completed,
            Creators = creators,
            Genres = genres,
            Categories = categories,
            Publishers = publishers,
            SiteUrl = SiteUrl(s.Url),
            ImageRemoteUrl = MangaUpdatesProvider.ImageUrl(s.Image?.Url?.Original),
            Publications = publications,
            Relations = relations,
            ProviderUpdatedAt = s.LastUpdated?.Timestamp is > 0 and < 253402300800
                ? DateTimeOffset.FromUnixTimeSeconds(s.LastUpdated.Timestamp.Value)
                : null,
        };
    }

    /// <summary>At most this many other names are kept per author (1.38.0).</summary>
    public const int MaxAuthorOtherNames = 50;

    /// <summary>Each name is cleaned to one line of at most this many characters (1.38.0).</summary>
    public const int MaxAuthorNameLength = 256;

    /// <summary>Placeholders MangaUpdates shows for an unknown real name; never kept as a name.</summary>
    private static readonly IReadOnlySet<string> s_noName = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "N/A", "NA", "-", "?", "Unknown", "None" };

    /// <summary>
    /// An author record -> its main name and other names (1.38.0): the associated names, then the name in its own script
    /// (<c>actualname</c>) when it is not among them; cleaned to one line, duplicates (case-insensitive) and the main name left out,
    /// at most <see cref="MaxAuthorOtherNames"/>. A record without a main name is unreadable.
    /// </summary>
    internal static ProviderAuthorRecord ToAuthor(MuAuthorRecord a, long requestedId)
    {
        var name = MetadataText.Line(a.Name, MaxAuthorNameLength) ?? throw new MetadataResponseInvalidException("missing_name");
        var others = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        foreach (var raw in (a.Associated ?? []).Select(x => x.Name).Append(a.ActualName))
        {
            if (others.Count >= MaxAuthorOtherNames)
                break;
            if (MetadataText.Line(raw, MaxAuthorNameLength) is { } other && !s_noName.Contains(other) && seen.Add(other))
                others.Add(other);
        }
        // Keyed by the id that was asked for - the id the stored series records name - even if MangaUpdates answers with another.
        return new ProviderAuthorRecord(requestedId.ToString(CultureInfo.InvariantCulture), name, others);
    }

    private static int? Max(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static string? SiteUrl(string? url) =>
        url is { Length: <= 512 } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && s_siteHosts.Contains(uri.Host)
            ? uri.AbsoluteUri
            : null;
}

/// <summary>A provider's author record (1.38.0): the main name and the other names, cleaned.</summary>
public sealed record ProviderAuthorRecord(string ExternalId, string Name, IReadOnlyList<string> OtherNames);
