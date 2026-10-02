namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.Gcd;

using System.Text.Json.Serialization;

// Grand Comics Database API payloads (1.32.0, lane B) - only the fields MangaPixer reads. The GCD API wiki says "the provided
// fields and data format should not be considered stable": every field is optional and a missing one is never an error.

/// <summary>A page of <c>/api/series/name/&lt;name&gt;/</c> (Django REST framework pagination, 50 per page).</summary>
internal sealed class GcdSeriesPage
{
    [JsonPropertyName("count")] public int? Count { get; set; }
    [JsonPropertyName("results")] public List<GcdSeries>? Results { get; set; }
}

/// <summary>A series - one edition of a run: an issue run, a collected series, a graphic novel or an album series, each translation its own.</summary>
internal sealed class GcdSeries
{
    [JsonPropertyName("api_url")] public string? ApiUrl { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("active_issues")] public List<string>? ActiveIssues { get; set; }
    [JsonPropertyName("issue_descriptors")] public List<string>? IssueDescriptors { get; set; }
    [JsonPropertyName("binding")] public string? Binding { get; set; }
    [JsonPropertyName("publishing_format")] public string? PublishingFormat { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("year_began")] public int? YearBegan { get; set; }
    [JsonPropertyName("year_ended")] public int? YearEnded { get; set; }
    [JsonPropertyName("publisher")] public string? Publisher { get; set; }
}

/// <summary>An issue (book) - read only for the chosen candidate: its cover thumbnail, credits, page count and ISBN.</summary>
internal sealed class GcdIssue
{
    [JsonPropertyName("number")] public string? Number { get; set; }
    [JsonPropertyName("page_count")] public string? PageCount { get; set; }
    [JsonPropertyName("indicia_publisher")] public string? IndiciaPublisher { get; set; }
    [JsonPropertyName("isbn")] public string? Isbn { get; set; }
    [JsonPropertyName("key_date")] public string? KeyDate { get; set; }
    [JsonPropertyName("story_set")] public List<GcdStory>? Stories { get; set; }
    [JsonPropertyName("cover")] public string? Cover { get; set; }
}

internal sealed class GcdStory
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("script")] public string? Script { get; set; }
    [JsonPropertyName("pencils")] public string? Pencils { get; set; }
}

internal sealed class GcdPublisher
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}
