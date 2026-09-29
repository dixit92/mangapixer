namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// MangaDex as a COMPANION source (1.29.0): the volume -> chapter list and the volume covers of a series that is
/// already linked to a MangaUpdates record. Deliberately NOT an <see cref="IMetadataProvider"/>: it is never offered to
/// Identify, the matcher or refresh, and no folder is ever linked to it. Like every provider it only translates
/// HTTP/JSON; the gateway (<see cref="MetadataGateway.CompanionCallAsync{T}"/> / <see cref="MetadataGateway.FetchImageAsync"/>)
/// owns every policy.
/// </summary>
public interface IMangaDexProvider
{
    /// <summary>Up to 10 records for a title - the linked MangaUpdates record's title or associated title, never a folder name.</summary>
    Task<IReadOnlyList<MangaDexManga>> SearchAsync(string title, CancellationToken ct);

    /// <summary>The record with this id, or null when MangaDex says it does not exist.</summary>
    Task<MangaDexManga?> GetAsync(string mangaId, CancellationToken ct);

    /// <summary>
    /// The volume -> chapter list, including chapters MangaDex does not host (<c>includeUnavailable=1</c>); with
    /// <paramref name="translatedLanguage"/> only the chapters released in that language (<c>translatedLanguage[]</c>).
    /// </summary>
    Task<IReadOnlyList<MangaDexAggregateVolume>> AggregateAsync(string mangaId, CancellationToken ct, string? translatedLanguage = null);

    /// <summary>One page (up to 100) of the record's covers, filtered to <paramref name="locales"/> when given.</summary>
    Task<MangaDexCoverPage> CoversAsync(string mangaId, IReadOnlyList<string>? locales, int offset, CancellationToken ct);
}

/// <summary>A MangaDex record, as much of it as the companion needs. Titles are data: bounded, never logged.</summary>
public sealed record MangaDexManga
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<string> AltTitles { get; init; } = [];

    /// <summary>The raw <c>links.mu</c> value (MangaUpdates id, base36 today), or null.</summary>
    public string? MangaUpdatesLink { get; init; }

    /// <summary>The <c>links.al</c> value (an AniList id) when it is a positive integer, else null.</summary>
    public string? AniListId { get; init; }

    /// <summary><c>originalLanguage</c> (a MangaDex locale code), validated.</summary>
    public string? OriginalLanguage { get; init; }

    public string? LastVolume { get; init; }
    public string? LastChapter { get; init; }
    public int? Year { get; init; }
    public string? Status { get; init; }

    /// <summary>Tagged "Fan Colored" (a colour fan edition; the cross-link tie-break prefers the original).</summary>
    public bool FanColored { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>The main cover's file name (<c>includes[]=cover_art</c>), validated, or null.</summary>
    public string? MainCoverFile { get; init; }

    /// <summary>The main cover's id, or null.</summary>
    public string? MainCoverId { get; init; }
}

/// <summary>One volume of an <c>aggregate</c> answer: its key and its chapters with their upload counts.</summary>
public sealed record MangaDexAggregateVolume(string Volume, int Count, IReadOnlyList<MangaDexAggregateChapter> Chapters);

public sealed record MangaDexAggregateChapter(string Chapter, int Count);

/// <summary>One cover of a <c>/cover</c> answer.</summary>
public sealed record MangaDexCover(string Id, string MangaId, string? Volume, string Locale, string FileName, DateTimeOffset? UpdatedAt);

public sealed record MangaDexCoverPage(IReadOnlyList<MangaDexCover> Covers, int Total);

/// <summary>
/// The MangaDex REST API (<c>https://api.mangadex.org</c>). Fixed requests, only these parameters:
/// <c>GET /manga?title=&amp;limit=10&amp;order[relevance]=desc&amp;contentRating[]=</c>(the fixed list of all four
/// ratings - acceptance is by exact id, so a rating filter could only hide the right record of an adult work)
/// <c>&amp;includes[]=cover_art</c>; <c>GET /manga/{id}?includes[]=cover_art</c>;
/// <c>GET /manga/{id}/aggregate?includeUnavailable=1</c> (1.29.0 RC: also with <c>&amp;translatedLanguage[]=</c>the preferred language, to
/// learn which chapters are released in it); <c>GET /cover?manga[]={id}&amp;locales[]=..&amp;order[volume]=asc&amp;limit=100&amp;offset=</c>.
/// Headers come from the named client (generic User-Agent, no cookies, no token, no <c>Via</c>). The JSON is read
/// defensively: MangaDex sends an empty LIST <c>[]</c> where an object is empty (<c>links</c>, <c>volumes</c>, <c>chapters</c>).
/// </summary>
public sealed partial class MangaDexProvider : IMangaDexProvider
{
    public const string ProviderId = MetadataProviderAllowlist.MangaDex;
    public const string ProviderName = "MangaDex";
    public const int SearchLimit = 10;
    public const int CoverPageSize = 100;

    /// <summary>The content ratings sent with every search (all four: acceptance is by the exact MangaUpdates id).</summary>
    public static readonly IReadOnlyList<string> ContentRatings = ["safe", "suggestive", "erotica", "pornographic"];

    private const string ApiBase = "https://" + MetadataHttp.MangaDexApiHost;
    private const string ImageBase = "https://" + MetadataHttp.MangaDexImageHost + "/covers/";
    private const int MaxTitleLength = 512;

    private readonly IHttpClientFactory _httpFactory;

    public MangaDexProvider(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex UuidPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,100}\.(?:jpg|jpeg|png|gif|webp)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FileNamePattern();

    [GeneratedRegex("^[a-z]{2,3}(-[a-z]{2,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalePattern();

    /// <summary>A MangaDex record or cover id (a lowercase UUID).</summary>
    public static bool IsValidId(string? id) => id is not null && UuidPattern().IsMatch(id);

    /// <summary>A MangaDex locale code as the settings accept it (<c>en</c>, <c>ja</c>, <c>pt-br</c>, <c>es-la</c>).</summary>
    public static bool IsValidLocale(string? locale) => locale is not null && LocalePattern().IsMatch(locale);

    public static bool IsValidFileName(string? fileName) => fileName is not null && FileNamePattern().IsMatch(fileName);

    /// <summary>
    /// The 512-pixel cover image address, rebuilt from the record id and file name MangaDex returned (never a URL
    /// taken from anywhere else); null when either is not valid.
    /// </summary>
    public static string? CoverImageUrl(string mangaId, string fileName) =>
        IsValidId(mangaId) && IsValidFileName(fileName) ? ImageBase + mangaId + "/" + fileName + ".512.jpg" : null;

    /// <summary>The public title page of a record (the admin's "Change MangaDex match..." link).</summary>
    public static string SiteUrl(string mangaId) => "https://mangadex.org/title/" + mangaId;

    /// <summary>
    /// Parses a pasted MangaDex reference LOCALLY: a bare UUID or a <c>mangadex.org/title/&lt;uuid&gt;[/slug]</c> URL.
    /// </summary>
    public static bool TryParseReference(string? input, out string mangaId)
    {
        mangaId = string.Empty;
        var text = (input ?? string.Empty).Trim();
        if (text.Length is 0 or > 512)
            return false;
        if (IsValidId(text.ToLowerInvariant()))
        {
            mangaId = text.ToLowerInvariant();
            return true;
        }
        var candidate = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !(uri.Host.Equals("mangadex.org", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.mangadex.org", StringComparison.OrdinalIgnoreCase)))
            return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[0].Equals("title", StringComparison.OrdinalIgnoreCase) && IsValidId(segments[1].ToLowerInvariant()))
        {
            mangaId = segments[1].ToLowerInvariant();
            return true;
        }
        return false;
    }

    /// <summary>The search address (exposed for the tests that pin what is sent).</summary>
    public static string SearchUrl(string title)
    {
        var sb = new StringBuilder(ApiBase).Append("/manga?title=").Append(Uri.EscapeDataString(title))
            .Append("&limit=").Append(SearchLimit.ToString(CultureInfo.InvariantCulture))
            .Append("&order%5Brelevance%5D=desc");
        foreach (var rating in ContentRatings)
            sb.Append("&contentRating%5B%5D=").Append(rating);
        return sb.Append("&includes%5B%5D=cover_art").ToString();
    }

    public async Task<IReadOnlyList<MangaDexManga>> SearchAsync(string title, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(SearchUrl(title), ct);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];
        return data.EnumerateArray().Select(ReadManga).OfType<MangaDexManga>().Take(SearchLimit).ToList();
    }

    public async Task<MangaDexManga?> GetAsync(string mangaId, CancellationToken ct)
    {
        if (!IsValidId(mangaId))
            return null;
        using var doc = await GetJsonAsync($"{ApiBase}/manga/{mangaId}?includes%5B%5D=cover_art", ct);
        return doc is not null && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? ReadManga(data)
            : null;
    }

    public async Task<IReadOnlyList<MangaDexAggregateVolume>> AggregateAsync(string mangaId, CancellationToken ct, string? translatedLanguage = null)
    {
        if (!IsValidId(mangaId) || (translatedLanguage is not null && !IsValidLocale(translatedLanguage)))
            return [];
        var language = translatedLanguage is null ? string.Empty : "&translatedLanguage%5B%5D=" + translatedLanguage;
        using var doc = await GetJsonAsync($"{ApiBase}/manga/{mangaId}/aggregate?includeUnavailable=1{language}", ct);
        return doc is null ? [] : ReadAggregate(doc.RootElement);
    }

    public async Task<MangaDexCoverPage> CoversAsync(string mangaId, IReadOnlyList<string>? locales, int offset, CancellationToken ct)
    {
        if (!IsValidId(mangaId))
            return new MangaDexCoverPage([], 0);
        var sb = new StringBuilder(ApiBase).Append("/cover?manga%5B%5D=").Append(mangaId);
        foreach (var locale in (locales ?? []).Where(IsValidLocale).Distinct(StringComparer.Ordinal))
            sb.Append("&locales%5B%5D=").Append(locale);
        sb.Append("&order%5Bvolume%5D=asc&limit=").Append(CoverPageSize.ToString(CultureInfo.InvariantCulture))
            .Append("&offset=").Append(Math.Clamp(offset, 0, 10_000).ToString(CultureInfo.InvariantCulture));
        using var doc = await GetJsonAsync(sb.ToString(), ct);
        return doc is null ? new MangaDexCoverPage([], 0) : ReadCovers(doc.RootElement);
    }

    /// <summary>One GET; null for a 404.</summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(MetadataHttp.MangaDexApiClient);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        MetadataHttp.EnsureSuccess(response);
        var bytes = await MetadataHttp.ReadBoundedAsync(response, MetadataHttp.MaxJsonBytes, ct);
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw new MetadataResponseInvalidException("malformed_json");
        }
    }

    // --- JSON readers (internal for the recorded-response tests) ---

    internal static MangaDexManga? ReadManga(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || Str(item, "id") is not { } id || !IsValidId(id)
            || !item.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
            return null;

        var titles = new List<string>();
        if (attributes.TryGetProperty("title", out var title))
            titles.AddRange(LocalizedValues(title, preferEnglish: true));
        if (attributes.TryGetProperty("altTitles", out var alts) && alts.ValueKind == JsonValueKind.Array)
            foreach (var alt in alts.EnumerateArray())
                titles.AddRange(LocalizedValues(alt, preferEnglish: false));
        var bounded = titles.Select(t => MetadataText.Line(t, MaxTitleLength)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (bounded.Count == 0)
            return null;

        string? mu = null, al = null;
        if (attributes.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object)
        {
            mu = Str(links, "mu") is { Length: > 0 and <= 32 } m ? m.Trim() : null;
            al = Str(links, "al") is { } a && int.TryParse(a.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var alId) && alId > 0
                ? alId.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        var fanColored = false;
        if (attributes.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.TryGetProperty("attributes", out var tagAttributes) && tagAttributes.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.Object && Str(name, "en") is { } en
                    && en.Equals("Fan Colored", StringComparison.OrdinalIgnoreCase))
                    fanColored = true;
            }
        }

        string? coverFile = null, coverId = null;
        if (item.TryGetProperty("relationships", out var relationships) && relationships.ValueKind == JsonValueKind.Array)
        {
            foreach (var rel in relationships.EnumerateArray())
            {
                if (Str(rel, "type") == "cover_art" && rel.TryGetProperty("attributes", out var coverAttributes)
                    && coverAttributes.ValueKind == JsonValueKind.Object && Str(coverAttributes, "fileName") is { } file && IsValidFileName(file))
                {
                    coverFile = file;
                    coverId = Str(rel, "id") is { } relId && IsValidId(relId) ? relId : null;
                    break;
                }
            }
        }

        var language = Str(attributes, "originalLanguage")?.Trim().ToLowerInvariant();
        return new MangaDexManga
        {
            Id = id,
            Title = bounded[0],
            AltTitles = bounded.Skip(1).Take(40).ToList(),
            MangaUpdatesLink = mu,
            AniListId = al,
            OriginalLanguage = IsValidLocale(language) ? language : null,
            LastVolume = MetadataText.Line(Str(attributes, "lastVolume"), 16),
            LastChapter = MetadataText.Line(Str(attributes, "lastChapter"), 16),
            Year = attributes.TryGetProperty("year", out var year) && year.ValueKind == JsonValueKind.Number && year.TryGetInt32(out var y)
                && y is >= 1900 and <= 2200 ? y : null,
            Status = MetadataText.Line(Str(attributes, "status"), 32),
            FanColored = fanColored,
            CreatedAt = DateTimeOffset.TryParse(Str(attributes, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)
                ? created : null,
            MainCoverFile = coverId is null ? null : coverFile,
            MainCoverId = coverFile is null ? null : coverId,
        };
    }

    internal static IReadOnlyList<MangaDexAggregateVolume> ReadAggregate(JsonElement root)
    {
        // "volumes" is an object keyed by the volume string, or an empty LIST when there is nothing.
        if (!root.TryGetProperty("volumes", out var volumes) || volumes.ValueKind != JsonValueKind.Object)
            return [];
        var result = new List<MangaDexAggregateVolume>();
        foreach (var volume in volumes.EnumerateObject())
        {
            if (volume.Value.ValueKind != JsonValueKind.Object)
                continue;
            var key = Str(volume.Value, "volume") ?? volume.Name;
            var chapters = new List<MangaDexAggregateChapter>();
            if (volume.Value.TryGetProperty("chapters", out var list))
            {
                IEnumerable<JsonElement> items = list.ValueKind switch
                {
                    JsonValueKind.Object => list.EnumerateObject().Select(p => p.Value),
                    JsonValueKind.Array => list.EnumerateArray(),
                    _ => [],
                };
                foreach (var chapter in items)
                {
                    if (chapter.ValueKind == JsonValueKind.Object && Str(chapter, "chapter") is { Length: > 0 and <= 16 } number)
                        chapters.Add(new MangaDexAggregateChapter(number, Int(chapter, "count") ?? 1));
                }
            }
            if (key.Length is > 0 and <= 16)
                result.Add(new MangaDexAggregateVolume(key, Int(volume.Value, "count") ?? chapters.Count, chapters));
        }
        return result;
    }

    internal static MangaDexCoverPage ReadCovers(JsonElement root)
    {
        var covers = new List<MangaDexCover>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (Str(item, "id") is not { } id || !IsValidId(id)
                    || !item.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object
                    || Str(attributes, "fileName") is not { } file || !IsValidFileName(file))
                    continue;
                var locale = Str(attributes, "locale")?.Trim().ToLowerInvariant();
                if (!IsValidLocale(locale))
                    continue;
                string? mangaId = null;
                if (item.TryGetProperty("relationships", out var relationships) && relationships.ValueKind == JsonValueKind.Array)
                    mangaId = relationships.EnumerateArray().Where(r => Str(r, "type") == "manga").Select(r => Str(r, "id")).FirstOrDefault(IsValidId);
                if (mangaId is null)
                    continue;
                covers.Add(new MangaDexCover(id, mangaId, MetadataText.Line(Str(attributes, "volume"), 16), locale!, file,
                    DateTimeOffset.TryParse(Str(attributes, "updatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updated)
                        ? updated : null));
            }
        }
        var total = Int(root, "total") ?? covers.Count;
        return new MangaDexCoverPage(covers, Math.Max(total, covers.Count));
    }

    /// <summary>The values of a MangaDex localized-string object (<c>{"en": "..."}</c>), English first when asked.</summary>
    private static IEnumerable<string> LocalizedValues(JsonElement value, bool preferEnglish)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return [];
        var pairs = value.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToList();
        if (preferEnglish)
            pairs = pairs.OrderBy(p => p.Name == "en" ? 0 : 1).ToList();
        return pairs.Select(p => p.Value.GetString()).OfType<string>();
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var n) ? n : null;
}
