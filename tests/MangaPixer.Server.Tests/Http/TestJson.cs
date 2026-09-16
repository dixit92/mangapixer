namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Shared JSON options for HTTP tests that deserialize enum-bearing DTOs.
/// The API serializes enums as their string names (JsonStringEnumConverter in
/// Program.cs); the default <c>ReadFromJsonAsync</c> options cannot parse string
/// enum values, so tests reading real Core DTOs with enum members must use these.
/// </summary>
internal static class TestJson
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
