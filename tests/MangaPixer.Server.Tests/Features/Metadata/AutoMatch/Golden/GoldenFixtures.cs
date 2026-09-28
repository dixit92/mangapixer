namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch.Golden;

using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using com.lifepixer.mangapixer.Server.Features.Metadata;

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
    /// The embedded image fixtures (1.28.0): <c>cover.&lt;stem&gt;.jpg</c> answers a provider image GET whose URL ends in
    /// <c>/&lt;stem&gt;.&lt;any extension&gt;</c> (tiny re-encodes of the recorded MangaUpdates thumbnails), and
    /// <c>local.&lt;series id&gt;.webp</c> is a local cover thumbnail made from that series' cover (cropped 3% per side).
    /// </summary>
    public static byte[]? Image(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + name);
        if (stream is null)
            return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Names of the embedded images starting with <paramref name="kind"/> (<c>cover.</c> or <c>local.</c>).</summary>
    public static IReadOnlyList<string> ImageNames(string kind) =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix + kind, StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToList();

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
        if (request.Method == HttpMethod.Get && uri.Host == MetadataHttp.MangaUpdatesImageHost)
        {
            var stem = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            if (Image("cover." + stem + ".jpg") is { } bytes)
                return ScriptedHandler.Bytes(bytes);
            lock (missing)
                missing.Add($"{{\"kind\":\"image\",\"url\":{JsonSerializer.Serialize(uri.ToString())}}}");
            return ScriptedHandler.Json("{\"reason\":\"not found\"}", HttpStatusCode.NotFound);
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
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
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
