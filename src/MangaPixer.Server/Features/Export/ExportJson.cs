namespace com.lifepixer.mangapixer.Server.Features.Export;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// The export's JSON conventions (1.33.0): camelCase names, every null written, times as UTC ISO 8601 with milliseconds and a
/// <c>Z</c>. One set of options writes the stored item JSON and every export response, so what a rebuild stored is what a client
/// reads, byte for byte.
/// </summary>
public static class ExportJson
{
    public const int SchemaVersion = 1;

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new UtcTimeConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static ExportItemDto? ReadItem(string json) => JsonSerializer.Deserialize<ExportItemDto>(json, Options);

    /// <summary>
    /// The fingerprint of an item: SHA-256 (hex) of its canonical JSON without the times that change on every rebuild that touches
    /// it (<c>updatedAt</c>, <c>completion.computedAt</c>, <c>completion.basedOnScanAt</c>), so an unchanged item keeps its row.
    /// </summary>
    public static string Fingerprint(ExportItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var stable = item with
        {
            UpdatedAt = default,
            Completion = item.Completion is null ? null : item.Completion with { ComputedAt = default, BasedOnScanAt = null },
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(stable)))).ToLowerInvariant();
    }

    /// <summary>A time truncated to whole milliseconds (what the wire format carries), in UTC.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset time)
    {
        var utc = time.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    /// <summary>Parses an <c>updatedSince</c> value (ISO 8601 with an offset or <c>Z</c>; no offset = UTC).</summary>
    public static bool TryParseTime(string? text, out DateTimeOffset time) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);

    public static string Format(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private sealed class UtcTimeConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            TryParseTime(reader.GetString(), out var time) ? time : throw new JsonException("invalid time");

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Format(value));
    }
}
