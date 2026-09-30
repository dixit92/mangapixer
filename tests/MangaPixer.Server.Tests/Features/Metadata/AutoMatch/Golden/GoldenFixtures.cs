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

    private static readonly Lazy<(Dictionary<(string Query, bool Doujin, int Page, string Extra), string> Searches, Dictionary<string, string> Series)> s_all = new(Load);

    /// <summary>
    /// The fixed automatic filter; anything else in a <c>filter_types</c> is part of the key (1.28.0 - 1.29.x: a declared type) - since
    /// 1.30.0 no recording has one, so a search that sends another type fails as a missing fixture.
    /// </summary>
    private static readonly HashSet<string> s_fixedTypes = new(StringComparer.Ordinal) { "Doujinshi", "Novel", "Artbook", "Drama CD" };

    private static string ExtraTypes(JsonElement types) =>
        string.Join(",", types.EnumerateArray().Select(t => t.GetString()!).Where(t => !s_fixedTypes.Contains(t)).Order(StringComparer.Ordinal));

    public static int SearchCount => s_all.Value.Searches.Count;

    public static int SeriesCount => s_all.Value.Series.Count;

    private static readonly Lazy<(Dictionary<string, ulong> Covers, Dictionary<string, ulong> Local)> s_hashes = new(LoadHashes);

    /// <summary>
    /// Stored cover hashes (1.28.0, <c>covers.json</c>): no cover art is kept in the repository (owner, 2026-09-28), only the
    /// 64-bit hashes the worker code computed from the recorded MangaUpdates thumbnails (by the image file name in the URL)
    /// and from local cover thumbnails made from a series' full cover (by series id).
    /// </summary>
    public static IReadOnlyDictionary<string, ulong> CoverHashes => s_hashes.Value.Covers;

    public static IReadOnlyDictionary<string, ulong> LocalHashes => s_hashes.Value.Local;

    private const string CoverMarker = "golden-cover:";
    private const string LocalMarker = "golden-local:";
    private static readonly byte[] s_png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// A placeholder "image" standing for a recorded cover: PNG magic bytes (the gateway's image check) and a marker that
    /// <see cref="RecordedCoverHasher"/> resolves to the stored hash. Nothing decodes it.
    /// </summary>
    public static byte[] CoverPlaceholder(string stem) => [.. s_png, .. System.Text.Encoding.ASCII.GetBytes(CoverMarker + stem)];

    /// <summary>The placeholder stored as a work's local cover thumbnail (series id of the cover it was made from).</summary>
    public static byte[] LocalPlaceholder(string seriesId) => [.. s_png, .. System.Text.Encoding.ASCII.GetBytes(LocalMarker + seriesId)];

    /// <summary>The stored hash a placeholder stands for, or null.</summary>
    public static ulong? HashOfPlaceholder(byte[] bytes)
    {
        if (bytes.Length <= s_png.Length)
            return null;
        var text = System.Text.Encoding.ASCII.GetString(bytes, s_png.Length, bytes.Length - s_png.Length);
        return text.StartsWith(CoverMarker, StringComparison.Ordinal) && CoverHashes.TryGetValue(text[CoverMarker.Length..], out var cover) ? cover
            : text.StartsWith(LocalMarker, StringComparison.Ordinal) && LocalHashes.TryGetValue(text[LocalMarker.Length..], out var local) ? local
            : null;
    }

    private static (Dictionary<string, ulong>, Dictionary<string, ulong>) LoadHashes()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + "covers.json")
            ?? throw new InvalidOperationException("covers.json is not embedded");
        using var doc = JsonDocument.Parse(stream);
        static Dictionary<string, ulong> Read(JsonElement e) => e.EnumerateObject()
            .ToDictionary(p => p.Name, p => ulong.Parse(p.Value.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture), StringComparer.Ordinal);
        return (Read(doc.RootElement.GetProperty("covers")), Read(doc.RootElement.GetProperty("local")));
    }

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
            var hasTypes = root.TryGetProperty("filter_types", out var types);
            var doujin = !hasTypes || !types.EnumerateArray().Any(t => t.GetString() == "Doujinshi");
            var extra = hasTypes ? ExtraTypes(types) : string.Empty;
            if (s_all.Value.Searches.TryGetValue((query, doujin, page, extra), out var json))
                return ScriptedHandler.Json(json);
            lock (missing)
                missing.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{{\"kind\":\"search\",\"query\":{JsonSerializer.Serialize(query)},\"doujin\":{(doujin ? "true" : "false")},\"page\":{page},\"extra\":{JsonSerializer.Serialize(extra)}}}"));
            return ScriptedHandler.Json("{\"total_hits\":0,\"results\":[]}");
        }
        if (request.Method == HttpMethod.Get && uri.Host == MetadataHttp.MangaUpdatesImageHost)
        {
            var stem = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            if (CoverHashes.ContainsKey(stem))
                return ScriptedHandler.Bytes(CoverPlaceholder(stem));
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

    private static (Dictionary<(string, bool, int, string), string>, Dictionary<string, string>) Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        var searches = new Dictionary<(string, bool, int, string), string>();
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
                searches[(query, doujin, page, ExtraTypes(root.GetProperty("filter_types")))] = root.GetProperty("response").GetRawText();
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
