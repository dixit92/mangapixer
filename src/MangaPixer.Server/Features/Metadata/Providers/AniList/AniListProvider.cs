namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.AniList;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// A source of volume and chapter totals for the chapters-per-volume conversion of the Missing report (1.28.0).
/// Deliberately NOT an <see cref="IMetadataProvider"/>: it is never offered to Identify, auto-match or refresh, and
/// no folder is ever linked to it. Like every provider it only translates HTTP/JSON; the gateway
/// (<see cref="MetadataGateway.ConversionCallAsync{T}"/>) owns every policy.
/// </summary>
public interface IUnitConversionProvider
{
    /// <summary>Slug stored in <c>metadata_records.Provider</c> and used by the allowlist (<c>anilist</c>).</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Up to 5 entries for a title (the linked record's title, never a folder name).</summary>
    Task<IReadOnlyList<ConversionCandidate>> SearchAsync(string title, CancellationToken ct);

    /// <summary>The entry with this id, or null when the provider says it does not exist.</summary>
    Task<ConversionCandidate?> GetAsync(string externalId, CancellationToken ct);
}

/// <summary>One provider entry with its totals. Titles are data: bounded, never logged.</summary>
public sealed record ConversionCandidate(
    string ExternalId,
    string Title,
    IReadOnlyList<string> AltTitles,
    int? Volumes,
    int? Chapters,
    MetadataOriginStatus? Status,
    int? StartYear,
    string? Format,
    string? SiteUrl);

/// <summary>
/// AniList (GraphQL, <c>POST https://graphql.anilist.co</c>). Two fixed queries, variables only: <c>Media(id)</c>
/// with the numeric id, or <c>Page(perPage: 5) { media(search, type: MANGA, format_not: NOVEL) }</c> with the
/// search text. Nothing else is sent (headers come from the named client: generic User-Agent, no cookies, no
/// token). Reads titles, format, status, volumes, chapters, start year and the site URL.
/// </summary>
internal sealed class AniListProvider : IUnitConversionProvider
{
    public const string ProviderId = MetadataProviderAllowlist.AniList;
    public const string ProviderName = "AniList";
    private const string Endpoint = "https://" + MetadataHttp.AniListHost + "/";

    private const string Fields = "id title { romaji english native } synonyms format status volumes chapters startDate { year } siteUrl";

    internal const string ByIdQuery = "query ($id: Int) { Media(id: $id, type: MANGA) { " + Fields + " } }";

    internal const string SearchQuery =
        "query ($search: String) { Page(perPage: 5) { media(search: $search, type: MANGA, format_not: NOVEL, sort: SEARCH_MATCH) { " + Fields + " } } }";

    private readonly IHttpClientFactory _httpFactory;

    public AniListProvider(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public string Id => ProviderId;
    public string DisplayName => ProviderName;

    public async Task<IReadOnlyList<ConversionCandidate>> SearchAsync(string title, CancellationToken ct)
    {
        var body = await PostAsync(new GraphQlRequest(SearchQuery, new Dictionary<string, object> { ["search"] = title }), ct);
        return (body?.Data?.Page?.Media ?? []).Select(ToCandidate).OfType<ConversionCandidate>().Take(5).ToList();
    }

    public async Task<ConversionCandidate?> GetAsync(string externalId, CancellationToken ct)
    {
        if (!int.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return null;
        var body = await PostAsync(new GraphQlRequest(ByIdQuery, new Dictionary<string, object> { ["id"] = id }), ct);
        return body?.Data?.Media is { } media ? ToCandidate(media) : null;
    }

    /// <summary>One POST; null for a 404 (AniList answers "Not Found." for an unknown id).</summary>
    private async Task<GraphQlResponse?> PostAsync(GraphQlRequest request, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(MetadataHttp.AniListClient);
        using var content = JsonContent.Create(request);
        using var response = await client.PostAsync(Endpoint, content, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        MetadataHttp.EnsureSuccess(response);
        var bytes = await MetadataHttp.ReadBoundedAsync(response, MetadataHttp.MaxJsonBytes, ct);
        try
        {
            return JsonSerializer.Deserialize<GraphQlResponse>(bytes) ?? throw new MetadataResponseInvalidException("empty_body");
        }
        catch (JsonException)
        {
            throw new MetadataResponseInvalidException("malformed_json");
        }
    }

    internal static ConversionCandidate? ToCandidate(AlMedia media)
    {
        if (media.Id is not { } id || id <= 0)
            return null;
        var titles = new[] { media.Title?.English, media.Title?.Romaji, media.Title?.Native }
            .Concat(media.Synonyms ?? [])
            .Select(t => MetadataText.Line(t, 512))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (titles.Count == 0)
            return null;
        var siteUrl = media.SiteUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, "anilist.co", StringComparison.OrdinalIgnoreCase)
                ? uri.ToString()
                : null;
        return new ConversionCandidate(
            id.ToString(CultureInfo.InvariantCulture),
            titles[0],
            titles.Skip(1).Take(30).ToList(),
            media.Volumes is > 0 and < 10_000 ? media.Volumes : null,
            media.Chapters is > 0 and < 100_000 ? media.Chapters : null,
            media.Status switch
            {
                "FINISHED" => MetadataOriginStatus.Complete,
                "RELEASING" => MetadataOriginStatus.Ongoing,
                "HIATUS" => MetadataOriginStatus.Hiatus,
                "CANCELLED" => MetadataOriginStatus.Cancelled,
                _ => null,
            },
            media.StartDate?.Year is >= 1900 and <= 2200 ? media.StartDate.Year : null,
            MetadataText.Line(media.Format, 32),
            siteUrl);
    }

    private sealed record GraphQlRequest(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("variables")] Dictionary<string, object> Variables);

    internal sealed record GraphQlResponse([property: JsonPropertyName("data")] AlData? Data);

    internal sealed record AlData(
        [property: JsonPropertyName("Media")] AlMedia? Media,
        [property: JsonPropertyName("Page")] AlPage? Page);

    internal sealed record AlPage([property: JsonPropertyName("media")] List<AlMedia>? Media);

    internal sealed record AlMedia
    {
        [JsonPropertyName("id")] public int? Id { get; init; }
        [JsonPropertyName("title")] public AlTitle? Title { get; init; }
        [JsonPropertyName("synonyms")] public List<string>? Synonyms { get; init; }
        [JsonPropertyName("format")] public string? Format { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("volumes")] public int? Volumes { get; init; }
        [JsonPropertyName("chapters")] public int? Chapters { get; init; }
        [JsonPropertyName("startDate")] public AlDate? StartDate { get; init; }
        [JsonPropertyName("siteUrl")] public string? SiteUrl { get; init; }
    }

    internal sealed record AlTitle(
        [property: JsonPropertyName("romaji")] string? Romaji,
        [property: JsonPropertyName("english")] string? English,
        [property: JsonPropertyName("native")] string? Native);

    internal sealed record AlDate([property: JsonPropertyName("year")] int? Year);
}
