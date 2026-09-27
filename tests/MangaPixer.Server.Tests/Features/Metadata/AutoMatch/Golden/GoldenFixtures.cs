namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch.Golden;

using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;

/// <summary>
/// The recorded MangaUpdates responses of the golden set (embedded
/// <c>Features/Metadata/Fixtures/GoldenSet/*.json</c>, see the README there), replayed as HTTP answers.
/// Since 1.27.0 nothing here maps provider JSON: the answers go through the production MangaUpdates
/// provider, its wire models and <c>MangaUpdatesMapping</c>, and <c>AutoMatchLookup.ToCandidate</c>, so a
/// field the production mapping drops (the chapter total, the webtoon flag) fails the golden set instead of
/// passing it on a test-only parser.
/// </summary>
internal static class GoldenFixtures
{
    private const string Prefix = "GoldenSet.";

    private static readonly Lazy<(Dictionary<(string Query, bool Doujin, int Page), string> Searches, Dictionary<string, string> Series)> s_all = new(Load);

    public static int SearchCount => s_all.Value.Searches.Count;

    public static int SeriesCount => s_all.Value.Series.Count;

    /// <summary>
    /// Answers one provider request from the recordings. A request without a recording is added to
    /// <paramref name="missing"/> (the exact request the harness needs, so the set stays reproducible) and
    /// answered with no hits / 404.
    /// </summary>
    public static HttpResponseMessage Respond(HttpRequestMessage request, ICollection<string> missing)
    {
        var uri = request.RequestUri!;
        if (request.Method == HttpMethod.Post && uri.AbsolutePath == "/v1/series/search")
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = body.RootElement;
            var query = root.GetProperty("search").GetString()!;
            var page = root.TryGetProperty("page", out var p) ? p.GetInt32() : 1;
            var doujin = !root.TryGetProperty("filter_types", out var types)
                || !types.EnumerateArray().Any(t => t.GetString() == "Doujinshi");
            if (s_all.Value.Searches.TryGetValue((query, doujin, page), out var json))
                return ScriptedHandler.Json(json);
            lock (missing)
                missing.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{{\"kind\":\"search\",\"query\":{JsonSerializer.Serialize(query)},\"doujin\":{(doujin ? "true" : "false")},\"page\":{page}}}"));
            return ScriptedHandler.Json("{\"total_hits\":0,\"results\":[]}");
        }
        if (request.Method == HttpMethod.Get && uri.AbsolutePath.StartsWith("/v1/series/", StringComparison.Ordinal))
        {
            var id = uri.Segments[^1];
            if (s_all.Value.Series.TryGetValue(id, out var json))
                return ScriptedHandler.Json(json);
            lock (missing)
                missing.Add($"{{\"kind\":\"get\",\"id\":\"{id}\"}}");
            return ScriptedHandler.Json("{\"reason\":\"not found\"}", HttpStatusCode.NotFound);
        }
        lock (missing)
            missing.Add($"{{\"kind\":\"unexpected\",\"method\":\"{request.Method}\",\"path\":\"{uri.AbsolutePath}\"}}");
        return ScriptedHandler.Json("{\"reason\":\"not found\"}", HttpStatusCode.NotFound);
    }

    private static (Dictionary<(string, bool, int), string>, Dictionary<string, string>) Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        var searches = new Dictionary<(string, bool, int), string>();
        var series = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (name.StartsWith(Prefix + "search.", StringComparison.Ordinal))
            {
                var query = root.GetProperty("query").GetString()!;
                var doujin = !root.GetProperty("filter_types").EnumerateArray().Any(t => t.GetString() == "Doujinshi");
                var page = root.TryGetProperty("page", out var p) ? p.GetInt32() : 1;
                searches[(query, doujin, page)] = root.GetProperty("response").GetRawText();
            }
            else if (name.StartsWith(Prefix + "series.", StringComparison.Ordinal))
            {
                var id = root.GetProperty("series_id");
                series[id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString(CultureInfo.InvariantCulture) : id.GetString()!] = root.GetRawText();
            }
        }
        return (searches, series);
    }
}
