namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using System.Text.Json;

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

    /// <summary>A person credit: <c>{name, role}</c>.</summary>
    public sealed record Creator(string Name, string Role);

    /// <summary>A publisher credit: <c>{name, kind}</c>.</summary>
    public sealed record Publisher(string Name, string Kind);

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
