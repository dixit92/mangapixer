namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;

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
    [JsonPropertyName("type")] public string? Type { get; init; }
}

internal sealed record MuPublisher
{
    [JsonPropertyName("publisher_name")] public string? PublisherName { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
}

internal sealed record MuTime
{
    [JsonPropertyName("timestamp")] public long? Timestamp { get; init; }
}
