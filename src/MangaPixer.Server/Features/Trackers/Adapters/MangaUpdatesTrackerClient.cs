namespace com.lifepixer.mangapixer.Server.Features.Trackers.Adapters;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Logging;

/// <summary>
/// MangaUpdates lists (<c>api.mangaupdates.com</c>, the metadata host - a NEW kind of request there), 1.43.0 lane A, from the public
/// <c>openapi.yaml</c> only. Stateless.
/// <list type="bullet">
/// <item>Connect: <c>PUT /v1/account/login</c> with the username and password the user typed - used ONCE for the session token, never
/// stored, never logged, never in an exception. The spec states no token lifetime: <see cref="TrackerTokens.ExpiresAt"/> is null and there
/// is no refresh token (its <c>/account/refresh</c> refreshes a cookie session, not a bearer token).</item>
/// <item>Entries: <c>GET /v1/lists/series/{id}</c> (404 = on no list); add <c>POST /v1/lists/series</c>, update
/// <c>POST /v1/lists/series/update</c>, each with <c>series.id</c>, <c>list_id</c> and <c>status.chapter</c> / <c>status.volume</c> only.
/// No dates (the list item has none), no rating, no priority; no private flag (privacy is per list on MangaUpdates, not per entry).
/// A list entry carries no series totals: <see cref="TrackerEntry.TotalChapters"/> / <see cref="TrackerEntry.TotalVolumes"/> stay null
/// and <see cref="TrackerEntry.SeriesFinished"/> false - the engine takes them from the stored series record.</item>
/// <item>Writes answer <c>412 "Five second update delay"</c> -> rate limited for 5 s.</item>
/// <item>1.53.0 (lane T) scores - the user's own series rating, from the same <c>openapi.yaml</c> (read 2026-11-10): read
/// <c>GET /v1/series/{id}/rating</c> (<c>SeriesRatingModelV1 {rating}</c>; 404 = none), set <c>PUT /v1/series/{id}/rating</c> with ONLY
/// <c>{"rating": n}</c>, remove <c>DELETE /v1/series/{id}/rating</c>. "Take my scores" reads the user's lists (<c>GET /v1/lists</c>) and then
/// each list's items page by page (<c>POST /v1/lists/{id}/search</c> with ONLY <c>{"page", "perpage"}</c>), whose
/// <c>metadata.user_rating</c> is the user's rating of that series - one request per <see cref="ScorePageSize"/> entries, never one per series.
/// MangaUpdates allows decimals: a rating is rounded to MangaPixer's whole steps (<see cref="TrackerScoreRules.FromTracker"/>).</item>
/// <item>Calls share the metadata fetcher's <c>api.mangaupdates.com</c> bucket (<see cref="TrackerPacing.MangaUpdates"/>) and do not
/// count in its daily budget.</item>
/// </list>
/// </summary>
public sealed class MangaUpdatesTrackerClient : ITrackerClient, ITrackerScores
{
    public const string ApiBase = "https://" + MetadataHttp.MangaUpdatesApiHost + "/v1/";

    /// <summary>MangaUpdates' five-second write delay (its 412 answer).</summary>
    public static readonly TimeSpan UpdateDelay = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpFactory;
    private readonly TrackerPacing _pacing;
    private readonly TimeProvider _time;
    private readonly ILogger<MangaUpdatesTrackerClient> _logger;

    public MangaUpdatesTrackerClient(IHttpClientFactory httpFactory, TrackerPacing pacing, TimeProvider time, ILogger<MangaUpdatesTrackerClient> logger)
    {
        _httpFactory = httpFactory;
        _pacing = pacing;
        _time = time;
        _logger = logger;
    }

    public string Slug => TrackerSlugs.MangaUpdates;

    public TrackerConnectKind ConnectKind => TrackerConnectKind.Password;

    public Uri? BuildAuthorizeUrl(TrackerAppSettings app, string state, string codeChallenge) => null;

    public async Task<TrackerTokens> ConnectAsync(TrackerAppSettings app, TrackerConnectRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password) || request.UserName.Length > 128
            || request.Password.Length > 256)
            throw new TrackerException(TrackerFailure.Rejected);
        // A dictionary, not a record: nothing here has a ToString that could print the password.
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["username"] = request.UserName.Trim(),
            ["password"] = request.Password,
        });
        using var message = new HttpRequestMessage(HttpMethod.Put, ApiBase + "account/login") { Content = Json(body) };
        using var response = await SendAsync(message, "connect", ct);
        if (!response.IsSuccessStatusCode)
        {
            // 401 "Invalid credentials" / 400: the user typed something MangaUpdates does not accept - ask again, never retry it.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                throw new TrackerException(TrackerFailure.Rejected, (int)response.StatusCode);
            throw Failure(response);
        }
        using var json = await TrackerHttp.ReadJsonAsync(response, ct);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("context", out var context) || context.ValueKind != JsonValueKind.Object
            || Str(context, "session_token") is not { Length: > 0 } token)
            throw new TrackerException(TrackerFailure.Unavailable, (int)response.StatusCode);
        return new TrackerTokens(token, null, null);
    }

    /// <summary>MangaUpdates has no bearer refresh and states no lifetime: the session token is kept until it is refused (401).</summary>
    public Task<TrackerTokens> RefreshAsync(TrackerAppSettings app, TrackerTokens tokens, CancellationToken ct) =>
        string.IsNullOrEmpty(tokens.AccessToken)
            ? throw new TrackerException(TrackerFailure.NeedsReconnect)
            : Task.FromResult(tokens);

    public async Task<TrackerIdentity> WhoAmIAsync(TrackerTokens tokens, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "account/profile").WithBearer(tokens);
        using var response = await SendAsync(request, "whoami", ct);
        if (!response.IsSuccessStatusCode)
            throw Failure(response);
        using var json = await TrackerHttp.ReadJsonAsync(response, ct);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("user_id", out var id) || !id.TryGetInt64(out var userId) || userId <= 0
            || Str(root, "username") is not { Length: > 0 and <= 128 } name)
            throw new TrackerException(TrackerFailure.Unavailable, (int)response.StatusCode);
        return new TrackerIdentity(userId.ToString(CultureInfo.InvariantCulture), name);
    }

    public async Task<TrackerEntry?> ReadEntryAsync(TrackerTokens tokens, string mediaId, CancellationToken ct) =>
        (await ReadItemAsync(tokens, SeriesIdOf(mediaId), ct))?.Entry;

    /// <summary>
    /// Adds the series to the list of <paramref name="write"/>'s status, or updates it where it is. The current item is read first (one
    /// GET, no update delay) to choose add or update, and to keep a series on the user's own custom list when that list has the same
    /// type. Only <c>series.id</c>, <c>list_id</c> and the chapter / volume numbers are sent.
    /// </summary>
    public async Task WriteEntryAsync(TrackerTokens tokens, string mediaId, TrackerEntryWrite write, CancellationToken ct)
    {
        var seriesId = SeriesIdOf(mediaId);
        var current = await ReadItemAsync(tokens, seriesId, ct);
        var listId = current is { } item && item.Entry.Status == ListStatusOf(write.Status) ? item.ListId : ListIdOf(write.Status);
        var body = JsonSerializer.SerializeToUtf8Bytes(new[] { WriteBodyOf(seriesId, listId, write) });
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + (current is null ? "lists/series" : "lists/series/update"))
        {
            Content = Json(body),
        }.WithBearer(tokens);
        using var response = await SendAsync(request, current is null ? "add" : "update", ct);
        if (!response.IsSuccessStatusCode)
            throw Failure(response);
    }

    /// <summary>A rating is its own request (<c>GET /v1/series/{id}/rating</c>), not part of the list item.</summary>
    public bool ScoreInEntry => false;

    /// <summary>Entries asked for per list page of "Take my scores" (the spec states no maximum; the answer's own paging is followed).</summary>
    public const int ScorePageSize = 100;

    /// <summary>A safety bound on the user's lists read by "Take my scores" (the five standard lists and custom ones).</summary>
    public const int MaxScoreLists = 50;

    /// <summary><c>GET /v1/series/{id}/rating</c>: the user's rating, or null (404 - the spec's "not found" - or no number).</summary>
    public async Task<int?> ReadScoreAsync(TrackerTokens tokens, string mediaId, TrackerEntry entry, CancellationToken ct)
    {
        var seriesId = SeriesIdOf(mediaId);
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "series/" + seriesId.ToString(CultureInfo.InvariantCulture) + "/rating")
            .WithBearer(tokens);
        using var response = await SendAsync(request, "score_read", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw Failure(response);
        using var json = await TrackerHttp.ReadJsonAsync(response, ct);
        return RatingOf(json.RootElement, "rating");
    }

    /// <summary><c>PUT /v1/series/{id}/rating</c> with ONLY <c>{"rating": n}</c>, or <c>DELETE /v1/series/{id}/rating</c> for a clear.</summary>
    public async Task WriteScoreAsync(TrackerTokens tokens, string mediaId, int? score, CancellationToken ct)
    {
        var seriesId = SeriesIdOf(mediaId);
        if (!TrackerScoreRules.IsSendable(score))
            throw new TrackerException(TrackerFailure.Rejected);
        var url = ApiBase + "series/" + seriesId.ToString(CultureInfo.InvariantCulture) + "/rating";
        using var request = score is { } value
            ? new HttpRequestMessage(HttpMethod.Put, url) { Content = Json(JsonSerializer.SerializeToUtf8Bytes(ScoreBodyOf(value))) }.WithBearer(tokens)
            : new HttpRequestMessage(HttpMethod.Delete, url).WithBearer(tokens);
        using var response = await SendAsync(request, score is null ? "score_clear" : "score_write", ct);
        // A clear of a rating that is not there is done.
        if (score is null && response.StatusCode == HttpStatusCode.NotFound)
            return;
        if (!response.IsSuccessStatusCode)
            throw Failure(response);
    }

    /// <summary>The body of a rating write: ONLY the rating.</summary>
    public static Dictionary<string, object> ScoreBodyOf(int score) => new() { ["rating"] = score };

    /// <summary>
    /// One request of "Take my scores": the first (<paramref name="cursor"/> null) reads the user's lists (<c>GET /v1/lists</c>); each next one
    /// reads one page of one list (<c>POST /v1/lists/{id}/search</c>, <c>{"page", "perpage"}</c>) and keeps, per item, only the series id and
    /// <c>metadata.user_rating</c> (titles in the answer are never kept). The cursor (built here, opaque to the engine) is
    /// <c>&lt;list index&gt;:&lt;page&gt;:&lt;list ids&gt;</c>.
    /// </summary>
    public async Task<TrackerScorePage> ReadScorePageAsync(TrackerTokens tokens, string? cursor, CancellationToken ct)
    {
        if (cursor is null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "lists").WithBearer(tokens);
            using var response = await SendAsync(request, "lists", ct);
            if (!response.IsSuccessStatusCode)
                throw Failure(response);
            using var json = await TrackerHttp.ReadJsonAsync(response, ct);
            var ids = ListIdsOf(json.RootElement);
            return new TrackerScorePage([], ids.Count == 0 ? null : ScoreCursor(0, 1, ids));
        }

        var (index, page, lists) = ParseScoreCursor(cursor);
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["page"] = page, ["perpage"] = ScorePageSize });
        using var search = new HttpRequestMessage(HttpMethod.Post, ApiBase + "lists/" + lists[index].ToString(CultureInfo.InvariantCulture) + "/search")
        {
            Content = Json(body),
        }.WithBearer(tokens);
        using var answer = await SendAsync(search, "list_page", ct);
        if (!answer.IsSuccessStatusCode)
            throw Failure(answer);
        using var result = await TrackerHttp.ReadJsonAsync(answer, TrackerHttp.MaxListPageBytes, ct);
        var (items, more) = ScorePageOf(result.RootElement, page);
        string? next = more && page < MaxScorePagesPerList ? ScoreCursor(index, page + 1, lists) : index + 1 < lists.Count ? ScoreCursor(index + 1, 1, lists) : null;
        return new TrackerScorePage(items, next);
    }

    /// <summary>The user's list ids in a <c>GET /v1/lists</c> answer (an array of <c>ListsModelV1</c>), at most <see cref="MaxScoreLists"/>.</summary>
    public static IReadOnlyList<int> ListIdsOf(JsonElement root)
    {
        var ids = new List<int>();
        if (root.ValueKind != JsonValueKind.Array)
            return ids;
        foreach (var list in root.EnumerateArray())
        {
            if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("list_id", out var id) && id.ValueKind == JsonValueKind.Number
                && id.TryGetInt32(out var value) && value >= 0 && !ids.Contains(value))
                ids.Add(value);
            if (ids.Count == MaxScoreLists)
                break;
        }
        return ids;
    }

    /// <summary>
    /// A list page (<c>ListsSearchResponseV1</c>): per item the series id (<c>record.series.id</c>) and the user's rating
    /// (<c>metadata.user_rating</c>), unrated items left out; and whether another page follows (<c>total_hits</c> beyond this page).
    /// </summary>
    public static (IReadOnlyList<TrackerScoreItem> Items, bool More) ScorePageOf(JsonElement root, int page)
    {
        var items = new List<TrackerScoreItem>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return (items, false);
        var count = 0;
        foreach (var item in results.EnumerateArray())
        {
            count++;
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object
                || !record.TryGetProperty("series", out var series) || series.ValueKind != JsonValueKind.Object
                || !series.TryGetProperty("id", out var id) || !id.TryGetInt64(out var seriesId) || seriesId <= 0
                || !item.TryGetProperty("metadata", out var metadata) || RatingOf(metadata, "user_rating") is not { } score)
                continue;
            items.Add(new TrackerScoreItem(seriesId.ToString(CultureInfo.InvariantCulture), score));
        }
        var perPage = root.TryGetProperty("per_page", out var p) && p.TryGetInt32(out var pp) && pp > 0 ? pp : ScorePageSize;
        var total = root.TryGetProperty("total_hits", out var t) && t.TryGetInt32(out var hits) && hits >= 0 ? hits : 0;
        return (items, count > 0 && (long)page * perPage < total);
    }

    private static int? RatingOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var rating)
            ? TrackerScoreRules.FromTracker(rating)
            : null;

    /// <summary>The bound on pages per list (100 entries each).</summary>
    public const int MaxScorePagesPerList = 200;

    private static string ScoreCursor(int index, int page, IReadOnlyList<int> lists) =>
        string.Create(CultureInfo.InvariantCulture, $"{index}:{page}:{string.Join(',', lists)}");

    private static (int Index, int Page, IReadOnlyList<int> Lists) ParseScoreCursor(string cursor)
    {
        var parts = cursor.Split(':');
        if (parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page is >= 1 and <= MaxScorePagesPerList)
        {
            var lists = new List<int>();
            foreach (var part in parts[2].Split(','))
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var list))
                    throw new TrackerException(TrackerFailure.Rejected);
                lists.Add(list);
            }
            if (lists.Count is > 0 and <= MaxScoreLists && index < lists.Count)
                return (index, page, lists);
        }
        throw new TrackerException(TrackerFailure.Rejected);
    }

    /// <summary><c>POST /v1/account/logout</c>: ends the session token. A 401 means it had already ended.</summary>
    public async Task RevokeAsync(TrackerAppSettings app, TrackerTokens tokens, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "account/logout").WithBearer(tokens);
        using var response = await SendAsync(request, "revoke", ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Unauthorized)
            throw Failure(response);
    }

    /// <summary>One list item to send: ONLY the series id, the list and the chapter / volume numbers that are set.</summary>
    public static Dictionary<string, object> WriteBodyOf(long seriesId, int listId, TrackerEntryWrite write)
    {
        var body = new Dictionary<string, object>
        {
            ["series"] = new Dictionary<string, object> { ["id"] = seriesId },
            ["list_id"] = listId,
        };
        var status = new Dictionary<string, object>();
        if (write.VolumesRead is { } volumes)
            status["volume"] = Math.Max(0, volumes);
        if (write.ChaptersRead is { } chapters)
            status["chapter"] = Math.Max(0, chapters);
        if (status.Count > 0)
            body["status"] = status;
        return body;
    }

    /// <summary>
    /// The standard list of a status: 0 reading, 1 wish (plan to read), 2 complete, 3 unfinished (dropped), 4 on hold. The spec names
    /// the list TYPES (<c>read</c>, <c>wish</c>, <c>complete</c>, <c>unfinished</c>, <c>hold</c>) but not the ids of the standard lists;
    /// these are the ids Mihon and others use. MangaUpdates has no re-read state: a re-read goes to the reading list.
    /// </summary>
    public static int ListIdOf(TrackerEntryStatus status) => status switch
    {
        TrackerEntryStatus.Reading or TrackerEntryStatus.Rereading => 0,
        TrackerEntryStatus.PlanToRead => 1,
        TrackerEntryStatus.Completed => 2,
        TrackerEntryStatus.Dropped => 3,
        TrackerEntryStatus.OnHold => 4,
        _ => throw new TrackerException(TrackerFailure.Rejected),
    };

    /// <summary>The status a list type stands for (a custom list has a type too), or null for an unknown type.</summary>
    public static TrackerEntryStatus? StatusOfType(string? type) => type switch
    {
        "read" => TrackerEntryStatus.Reading,
        "wish" => TrackerEntryStatus.PlanToRead,
        "complete" => TrackerEntryStatus.Completed,
        "unfinished" => TrackerEntryStatus.Dropped,
        "hold" => TrackerEntryStatus.OnHold,
        _ => null,
    };

    /// <summary>The status of a standard list id, or null for another (custom) list.</summary>
    public static TrackerEntryStatus? StatusOfListId(int listId) => listId switch
    {
        0 => TrackerEntryStatus.Reading,
        1 => TrackerEntryStatus.PlanToRead,
        2 => TrackerEntryStatus.Completed,
        3 => TrackerEntryStatus.Dropped,
        4 => TrackerEntryStatus.OnHold,
        _ => null,
    };

    /// <summary>A list item answer as an entry (type first, then a standard list id, else a custom list read as "reading").</summary>
    public static (TrackerEntry Entry, int ListId)? ItemOf(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        var listId = root.TryGetProperty("list_id", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var id) && id >= 0 ? id : -1;
        var status = StatusOfType(Str(root, "list_type")) ?? (listId >= 0 ? StatusOfListId(listId) : null) ?? TrackerEntryStatus.Reading;
        int? chapter = null, volume = null;
        if (root.TryGetProperty("status", out var progress) && progress.ValueKind == JsonValueKind.Object)
        {
            chapter = Count(progress, "chapter");
            volume = Count(progress, "volume");
        }
        return (new TrackerEntry(status, chapter, volume, null, null, false, null, null), listId >= 0 ? listId : ListIdOf(status));
    }

    private static TrackerEntryStatus ListStatusOf(TrackerEntryStatus status) =>
        status == TrackerEntryStatus.Rereading ? TrackerEntryStatus.Reading : status;

    private async Task<(TrackerEntry Entry, int ListId)?> ReadItemAsync(TrackerTokens tokens, long seriesId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "lists/series/" + seriesId.ToString(CultureInfo.InvariantCulture))
            .WithBearer(tokens);
        using var response = await SendAsync(request, "read", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw Failure(response);
        using var json = await TrackerHttp.ReadJsonAsync(response, ct);
        return ItemOf(json.RootElement) ?? throw new TrackerException(TrackerFailure.Unavailable, (int)response.StatusCode);
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string operation, CancellationToken ct) =>
        TrackerHttp.SendAsync(_httpFactory.CreateClient(TrackerHttp.MangaUpdatesClient), request, _pacing.MangaUpdates, _logger,
            LogEvents.Trackers.MangaUpdatesCall, Slug, operation, ct);

    /// <summary>MangaUpdates' answers: 412 is its five-second update delay; the rest as <see cref="TrackerHttp.Failure"/>.</summary>
    private TrackerException Failure(HttpResponseMessage response) => response.StatusCode == HttpStatusCode.PreconditionFailed
        ? new TrackerException(TrackerFailure.RateLimited, 412, UpdateDelay)
        : TrackerHttp.Failure(response, _time);

    private static ByteArrayContent Json(byte[] body) =>
        new(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" } } };

    private static long SeriesIdOf(string mediaId) =>
        long.TryParse(mediaId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw new TrackerException(TrackerFailure.Rejected);

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= 0 ? n : null;
}
