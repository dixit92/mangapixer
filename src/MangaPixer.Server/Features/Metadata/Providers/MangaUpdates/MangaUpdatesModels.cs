namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;

using System.Text.Json;
using System.Text.Json.Serialization;

// Wire models for the MangaUpdates API v1 (1.24.0, lane B2): only the fields the
// provider reads. System.Text.Json ignores every other field, so additive API
// changes cannot break parsing ("may change at any time", AUP). Every property is
// nullable: a missing field degrades to "unknown", never to an exception.

internal sealed record MuSearchRequest(
    [property: JsonPropertyName("search")] string Search,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("perpage")] int PerPage,
    [property: JsonPropertyName("filter_types"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? FilterTypes = null);

internal sealed record MuSearchResponse
{
    [JsonPropertyName("total_hits")] public int? TotalHits { get; init; }
    [JsonPropertyName("results")] public List<MuSearchResult>? Results { get; init; }
}

internal sealed record MuSearchResult
{
    [JsonPropertyName("record")] public MuSeries? Record { get; init; }
    [JsonPropertyName("hit_title")] public string? HitTitle { get; init; }
}

/// <summary>A series record: the search result subset, or the full GET <c>/v1/series/{id}</c> body.</summary>
internal sealed record MuSeries
{
    [JsonPropertyName("series_id")] public long? SeriesId { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("associated")] public List<MuTitle>? Associated { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("image")] public MuImage? Image { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("year")] public string? Year { get; init; }
    [JsonPropertyName("genres")] public List<MuGenre>? Genres { get; init; }
    [JsonPropertyName("categories")] public List<MuCategory>? Categories { get; init; }
    [JsonPropertyName("latest_chapter")] public double? LatestChapter { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("licensed")] public bool? Licensed { get; init; }
    [JsonPropertyName("completed")] public bool? Completed { get; init; }
    [JsonPropertyName("authors")] public List<MuAuthor>? Authors { get; init; }
    [JsonPropertyName("publishers")] public List<MuPublisher>? Publishers { get; init; }
    [JsonPropertyName("last_updated")] public MuTime? LastUpdated { get; init; }
    [JsonPropertyName("publications")] public List<MuPublication>? Publications { get; init; }
    [JsonPropertyName("related_series")] public List<MuRelatedSeries>? RelatedSeries { get; init; }
}

internal sealed record MuPublication
{
    [JsonPropertyName("publication_name")] public string? PublicationName { get; init; }
    [JsonPropertyName("publisher_name")] public string? PublisherName { get; init; }
}

internal sealed record MuRelatedSeries
{
    [JsonPropertyName("relation_type")] public string? RelationType { get; init; }
    [JsonPropertyName("related_series_id")] public long? RelatedSeriesId { get; init; }
}

internal sealed record MuTitle
{
    [JsonPropertyName("title")] public string? Title { get; init; }
}

internal sealed record MuImage
{
    [JsonPropertyName("url")] public MuImageUrl? Url { get; init; }
}

internal sealed record MuImageUrl
{
    [JsonPropertyName("original")] public string? Original { get; init; }
    [JsonPropertyName("thumb")] public string? Thumb { get; init; }
}

internal sealed record MuGenre
{
    [JsonPropertyName("genre")] public string? Genre { get; init; }
}

internal sealed record MuCategory
{
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("votes")] public int? Votes { get; init; }
    [JsonPropertyName("votes_plus")] public int? VotesPlus { get; init; }
    [JsonPropertyName("votes_minus")] public int? VotesMinus { get; init; }
}

internal sealed record MuAuthor
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("author_id")] public long? AuthorId { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
}

internal sealed record MuPublisher
{
    [JsonPropertyName("publisher_name")] public string? PublisherName { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }

    /// <summary>Free text such as "10 Volumes / 60 Chapters; Ongoing" (1.27.0: the English totals).</summary>
    [JsonPropertyName("notes")] public string? Notes { get; init; }
}

internal sealed record MuTime
{
    [JsonPropertyName("timestamp")] public long? Timestamp { get; init; }
}

/// <summary>
/// An author record, the body of GET <c>/v1/authors/{author_id}</c> (1.38.0, artists' other names): only the names are read. The
/// answer also carries birthday, birthplace, social links and comments - never read, never stored.
/// </summary>
internal sealed record MuAuthorRecord
{
    [JsonPropertyName("id")] public long? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("associated")] public List<MuAuthorName>? Associated { get; init; }

    /// <summary>The name in its own script (often missing from <see cref="Associated"/>), e.g. the kanji of a Japanese name.</summary>
    [JsonPropertyName("actualname")] public string? ActualName { get; init; }
}

internal sealed record MuAuthorName
{
    [JsonPropertyName("name")] public string? Name { get; init; }
}

/// <summary>
/// The body of <c>POST /v1/authors/search</c> (1.45.0, author search by name - owner-approved 2026-10-19, consent 7): exactly the search
/// text the admin confirmed, page 1 and 10 per page. A FIXED record type on purpose - no other field of the API's request
/// (<c>added_by</c>, <c>letter</c>, <c>genre</c>, <c>orderby</c>, <c>pending</c>) can be sent; a test asserts the exact JSON.
/// </summary>
internal sealed record MuAuthorSearchRequest(
    [property: JsonPropertyName("search")] string Search,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("perpage")] int PerPage)
{
    /// <summary>The only page sent.</summary>
    public const int FixedPage = 1;

    /// <summary>The only page size sent.</summary>
    public const int FixedPerPage = 10;

    public static MuAuthorSearchRequest For(string search) => new(search, FixedPage, FixedPerPage);
}

internal sealed record MuAuthorSearchResponse
{
    [JsonPropertyName("total_hits")] public int? TotalHits { get; init; }
    [JsonPropertyName("results")] public List<MuAuthorSearchResult>? Results { get; init; }
}

internal sealed record MuAuthorSearchResult
{
    [JsonPropertyName("record")] public MuAuthorSearchRecord? Record { get; init; }

    /// <summary>The name (main or other) the search matched.</summary>
    [JsonPropertyName("hit_name")] public string? HitName { get; init; }
}

/// <summary>An author in a search answer: only id, name, page address, genres and the series count are read.</summary>
internal sealed record MuAuthorSearchRecord
{
    [JsonPropertyName("id")] public long? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }

    /// <summary>Either plain strings or <c>{"genre": "..."}</c> objects - both are read (<see cref="MuGenreListConverter"/>).</summary>
    [JsonPropertyName("genres"), JsonConverter(typeof(MuGenreListConverter))] public List<string>? Genres { get; init; }

    [JsonPropertyName("stats")] public MuAuthorStats? Stats { get; init; }
}

internal sealed record MuAuthorStats
{
    [JsonPropertyName("total_series")] public int? TotalSeries { get; init; }
}

/// <summary>
/// Reads a genre list written as strings (<c>["Drama"]</c>) or as objects (<c>[{"genre": "Drama", ...}]</c>); anything else in the list is
/// skipped, a non-list is null. Tolerant on purpose: the wire model must never fail on a shape change of a field shown only as a hint.
/// </summary>
internal sealed class MuGenreListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return null;
        }
        var list = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    if (reader.GetString() is { } s)
                        list.Add(s);
                    break;
                case JsonTokenType.StartObject:
                    using (var doc = JsonDocument.ParseValue(ref reader))
                    {
                        if (doc.RootElement.TryGetProperty("genre", out var g) && g.ValueKind == JsonValueKind.String && g.GetString() is { } name)
                            list.Add(name);
                    }
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
