namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using System.Text.Json;
using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// Serialization of the metadata tables' bounded JSON TEXT columns (1.24.0).
/// Tolerant on read: a malformed column reads as empty rather than failing a
/// page, since these columns are display data.
/// </summary>
public static class MetadataJson
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// A person credit: <c>{name, role}</c>, plus the provider's own person id when it
    /// has one (stage 2, JSON-additive: merges spellings in a later facet index).
    /// </summary>
    public sealed record Creator(
        string Name,
        string Role,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderId = null);

    /// <summary>A serialization venue: <c>{name, publisher}</c> (MangaUpdates <c>publications</c>).</summary>
    public sealed record Publication(
        string Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Publisher = null);

    /// <summary>A related record: <c>{externalId, relation}</c> (sequel, prequel, side story, spin-off ...).</summary>
    public sealed record RelatedRecord(string ExternalId, string Relation);

    /// <summary>
    /// A publisher credit: <c>{name, kind}</c>, plus (1.28.0, JSON-additive) the volume / chapter totals an ENGLISH
    /// publisher's notes state ("10 Volumes / 60 Chapters; Ongoing") for the missing volumes / chapters report. 1.30.0
    /// (JSON-additive): the publisher's own <see cref="Status"/> of that edition (<c>ongoing</c>, <c>complete</c>, <c>hiatus</c>,
    /// <c>cancelled</c> - "Defunct" / "Dropped" read as cancelled), and <see cref="Omnibus"/> when only an omnibus / N-in-1 /
    /// perfect edition states a volume count (its numbering is not the original's, so <see cref="Volumes"/> stays null).
    /// Records fetched before 1.30.0 read both as null until their next refresh.
    /// </summary>
    public sealed record Publisher(
        string Name,
        string Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Volumes = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Chapters = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Omnibus = null)
    {
        /// <summary>The stored <see cref="Status"/> word of a status, or null for none / unknown.</summary>
        public static string? StatusWord(MetadataOriginStatus? status) => status switch
        {
            MetadataOriginStatus.Ongoing => "ongoing",
            MetadataOriginStatus.Complete => "complete",
            MetadataOriginStatus.Hiatus => "hiatus",
            MetadataOriginStatus.Cancelled => "cancelled",
            _ => null,
        };

        /// <summary>The parsed <see cref="Status"/>, or null.</summary>
        public MetadataOriginStatus? StatusValue => Status?.Trim().ToLowerInvariant() switch
        {
            "ongoing" => MetadataOriginStatus.Ongoing,
            "complete" => MetadataOriginStatus.Complete,
            "hiatus" => MetadataOriginStatus.Hiatus,
            "cancelled" => MetadataOriginStatus.Cancelled,
            _ => null,
        };
    }

    /// <summary>A provider category with its vote count: <c>{name, votes}</c>.</summary>
    public sealed record Category(string Name, int Votes);

    public static string? WriteList<T>(IReadOnlyCollection<T>? items) =>
        items is null || items.Count == 0 ? null : JsonSerializer.Serialize(items, s_options);

    public static IReadOnlyList<T> ReadList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, s_options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
