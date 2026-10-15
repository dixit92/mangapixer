namespace com.lifepixer.mangapixer.Server.Features.Trackers.Adapters;

using System.Globalization;
using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Logging;

/// <summary>
/// MyAnimeList (API v2, <c>api.myanimelist.net</c>; OAuth 2.0 on <c>myanimelist.net</c>), 1.43.0 lane A, from MAL's public API reference
/// and authorization guide only. Stateless: the engine owns the stored tokens, the OAuth <c>state</c> and the PKCE verifier.
/// <list type="bullet">
/// <item>Sign-in: authorization code + PKCE; MAL supports ONLY the <c>plain</c> method, so the challenge IS the verifier
/// (43-128 characters). The instance's own app (Client ID, optional secret, the exact registered redirect URL) - MangaPixer ships none.</item>
/// <item>Tokens: the guide's table says an access token lasts one hour and a refresh token one month while its sample answer has
/// <c>expires_in = 2415600</c> (about 28 days) - so <see cref="TrackerTokens.ExpiresAt"/> comes from each answer's <c>expires_in</c>.
/// A refresh returns a NEW refresh token (the old one stays usable until it expires).</item>
/// <item>Written fields: <c>status</c>, <c>num_chapters_read</c>, <c>num_volumes_read</c>, <c>is_rereading</c> (only for
/// <see cref="TrackerEntryStatus.Rereading"/>) and - 1.43.0, owner A1 - <c>start_date</c> / <c>finish_date</c> (<c>yyyy-MM-dd</c>) when the
/// write carries them. MAL's reference does not document the dates for this endpoint (Mihon sends them): when MAL answers a write WITH dates
/// with a 4xx other than 401 / 403 / 404 / 429, the write is sent once more without them, and when that succeeds this server sends no dates
/// to MAL again - also after a restart (<see cref="DatesRefused"/>, <see cref="ITrackerDatesMemory"/>). Never a score, tags, comments, priority or re-read counts; no private flag
/// (MAL's API has none - <see cref="TrackerEntryWrite.Private"/> is ignored here).</item>
/// <item>No revoke endpoint is documented: <see cref="RevokeAsync"/> sends nothing.</item>
/// <item>1.43.0 "Check my list": the user's whole list, paged (<see cref="ReadListPageAsync"/>).</item>
/// </list>
/// </summary>
public sealed class MyAnimeListTrackerClient : ITrackerClient, ITrackerDates, ITrackerListReader
{
    public const string AuthorizeUrl = "https://" + TrackerHttp.MyAnimeListSiteHost + "/v1/oauth2/authorize";
    public const string TokenUrl = "https://" + TrackerHttp.MyAnimeListSiteHost + "/v1/oauth2/token";
    public const string ApiBase = "https://" + TrackerHttp.MyAnimeListApiHost + "/v2/";

    /// <summary>The fields read for one series: its totals, its publishing status and the user's own list status.</summary>
    public const string EntryFields = "num_chapters,num_volumes,status,my_list_status";

    private readonly IHttpClientFactory _httpFactory;
    private readonly TrackerPacing _pacing;
    private readonly TimeProvider _time;
    private readonly ILogger<MyAnimeListTrackerClient> _logger;
    private readonly ITrackerDatesMemory? _datesMemory;

    public MyAnimeListTrackerClient(IHttpClientFactory httpFactory, TrackerPacing pacing, TimeProvider time, ILogger<MyAnimeListTrackerClient> logger,
        ITrackerDatesMemory? datesMemory = null)
    {
        _datesMemory = datesMemory;
        _httpFactory = httpFactory;
        _pacing = pacing;
        _time = time;
        _logger = logger;
    }

    public string Slug => TrackerSlugs.MyAnimeList;

    public TrackerConnectKind ConnectKind => TrackerConnectKind.OAuthPkce;

    public Uri? BuildAuthorizeUrl(TrackerAppSettings app, string state, string codeChallenge)
    {
        if (string.IsNullOrWhiteSpace(app.ClientId) || string.IsNullOrEmpty(state) || !IsPkceValue(codeChallenge))
            throw new TrackerException(TrackerFailure.Rejected);
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", app.ClientId.Trim()),
            new("state", state),
        };
        if (app.RedirectUri is { } redirect)
            query.Add(new("redirect_uri", RedirectOf(redirect)));
        query.Add(new("code_challenge", codeChallenge));
        query.Add(new("code_challenge_method", "plain"));
        return new Uri(AuthorizeUrl + "?" + string.Join('&', query.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value))));
    }

    public async Task<TrackerTokens> ConnectAsync(TrackerAppSettings app, TrackerConnectRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.ClientId) || string.IsNullOrEmpty(request.Code) || !IsPkceValue(request.CodeVerifier))
            throw new TrackerException(TrackerFailure.Rejected);
        var form = Credentials(app);
        form.Add(new("grant_type", "authorization_code"));
        form.Add(new("code", request.Code));
        form.Add(new("code_verifier", request.CodeVerifier!));
        if (app.RedirectUri is { } redirect)
            form.Add(new("redirect_uri", RedirectOf(redirect)));
        return await TokenAsync(form, "connect", refresh: null, ct);
    }

    public async Task<TrackerTokens> RefreshAsync(TrackerAppSettings app, TrackerTokens tokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.ClientId) || string.IsNullOrEmpty(tokens.RefreshToken))
            throw new TrackerException(TrackerFailure.NeedsReconnect);
        var form = Credentials(app);
        form.Add(new("grant_type", "refresh_token"));
        form.Add(new("refresh_token", tokens.RefreshToken));
        return await TokenAsync(form, "refresh", tokens.RefreshToken, ct);
    }

    public async Task<TrackerIdentity> WhoAmIAsync(TrackerTokens tokens, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "users/@me").WithBearer(tokens);
        using var response = await SendAsync(request, "whoami", ct);
        if (!response.IsSuccessStatusCode)
            throw Failure(response);
        using var json = await TrackerHttp.ReadJsonAsync(response, ct);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) || !id.TryGetInt64(out var userId) || userId <= 0
            || Str(root, "name") is not { Length: > 0 and <= 64 } name)
            throw new TrackerException(TrackerFailure.Unavailable, (int)response.StatusCode);
        return new TrackerIdentity(userId.ToString(CultureInfo.InvariantCulture), name);
    }

    public async Task<TrackerEntry?> ReadEntryAsync(TrackerTokens tokens, string mediaId, CancellationToken ct)
    {
        var id = MediaIdOf(mediaId);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}manga/{id}?fields={EntryFields}").WithBearer(tokens);
        using var response = await SendAsync(request, "read", ct);
        if (!response.IsSuccessStatusCode)
            throw Failure(response);
        using var json = await TrackerHttp.ReadJsonAsync(response, ct);
        return EntryOf(json.RootElement);
    }

    public async Task WriteEntryAsync(TrackerTokens tokens, string mediaId, TrackerEntryWrite write, CancellationToken ct)
    {
        var id = MediaIdOf(mediaId);
        var withDates = (write.StartedOn is not null || write.FinishedOn is not null) && !await DatesRefusedAsync(ct);
        using (var response = await SendWriteAsync(tokens, id, write, withDates, ct))
        {
            if (response.IsSuccessStatusCode)
                return;
            if (!withDates || !IsDateRefusal(response.StatusCode))
                throw Failure(response);
        }
        // MAL refused a write with dates (undocumented fields): once more without them, and none again when that works.
        using var retry = await SendWriteAsync(tokens, id, write, withDates: false, ct);
        if (!retry.IsSuccessStatusCode)
            throw Failure(retry);
        if (!_datesRefused)
        {
            _datesRefused = true;
            _logger.LogWarning(LogEvents.Trackers.DatesRejected, "Tracker {Tracker} refused start / finish dates; none are sent from now on", Slug);
            try
            {
                if (_datesMemory is not null)
                    await _datesMemory.RememberRefusedAsync(Slug, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Still remembered for this process; the next restart may try dates once more.
                _logger.LogWarning(LogEvents.Trackers.DatesRejected, "Tracker {Tracker} date refusal not stored: {ErrorType}", Slug, ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// True once MAL refused a write because of its dates and accepted it without them (owner A1 fallback): no dates are sent from then on.
    /// 1.43.0 owner answer 3: remembered across restarts (<see cref="ITrackerDatesMemory"/>, read lazily on the first dated write) and never
    /// cleared by the app.
    /// </summary>
    public bool DatesRefused => _datesRefused;

    private volatile bool _datesRefused;
    private volatile bool _datesMemoryRead;

    private async Task<bool> DatesRefusedAsync(CancellationToken ct)
    {
        if (_datesRefused || _datesMemoryRead || _datesMemory is null)
            return _datesRefused;
        if (await _datesMemory.IsRefusedAsync(Slug, ct))
            _datesRefused = true;
        _datesMemoryRead = true;
        return _datesRefused;
    }

    /// <summary>An answer that may be MAL refusing the dates: a 4xx that is not about the sign-in, the pace or the series.</summary>
    private static bool IsDateRefusal(HttpStatusCode status) =>
        (int)status is >= 400 and < 500
        && status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests);

    private async Task<HttpResponseMessage> SendWriteAsync(TrackerTokens tokens, string id, TrackerEntryWrite write, bool withDates, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{ApiBase}manga/{id}/my_list_status")
        {
            Content = new FormUrlEncodedContent(WriteFormOf(write, withDates)),
        }.WithBearer(tokens);
        return await SendAsync(request, "write", ct);
    }

    /// <summary>The fields of a whole-list read (1.43.0 "Check my list"): the user's list status of each entry and the series' totals.</summary>
    public const string ListFields = "list_status{status,num_chapters_read,num_volumes_read,start_date,finish_date,is_rereading},num_chapters,num_volumes,status";

    /// <summary>Entries per page of the whole-list read (MAL's maximum).</summary>
    public const int ListPageSize = 1000;

    public bool ReadsWholeList => true;

    /// <summary>
    /// One page of the user's own manga list (1.43.0 "Check my list", owner option C): <c>GET /v2/users/@me/mangalist</c> with
    /// <see cref="ListFields"/>, <c>limit=1000</c>, <c>offset</c> and <c>nsfw=true</c> (it is the user's own list - without it MAL silently
    /// leaves entries out). Each entry is read as <see cref="EntryOf"/> reads one series; a title in the answer is never kept. The next page's
    /// offset is taken from <c>paging.next</c> (only its <c>offset</c>; MangaPixer builds every URL itself).
    /// </summary>
    public async Task<TrackerListPage> ReadListPageAsync(TrackerTokens tokens, int offset, CancellationToken ct)
    {
        if (offset < 0)
            throw new TrackerException(TrackerFailure.Rejected);
        var url = ApiBase + "users/@me/mangalist?fields=" + Uri.EscapeDataString(ListFields) + "&limit=" + ListPageSize.ToString(CultureInfo.InvariantCulture)
            + "&offset=" + offset.ToString(CultureInfo.InvariantCulture) + "&nsfw=true";
        using var request = new HttpRequestMessage(HttpMethod.Get, url).WithBearer(tokens);
        using var response = await SendAsync(request, "list", ct);
        if (!response.IsSuccessStatusCode)
            throw Failure(response);
        using var json = await TrackerHttp.ReadJsonAsync(response, TrackerHttp.MaxListPageBytes, ct);
        return ListPageOf(json.RootElement);
    }

    /// <summary>A whole-list answer: <c>data[].node</c> (id, totals, publishing status) + <c>data[].list_status</c>, and <c>paging.next</c>.</summary>
    public static TrackerListPage ListPageOf(JsonElement root)
    {
        var items = new List<TrackerListItem>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("node", out var node) || node.ValueKind != JsonValueKind.Object
                    || !node.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id) || id <= 0
                    || !item.TryGetProperty("list_status", out var mine) || mine.ValueKind != JsonValueKind.Object)
                    continue;
                var rereading = mine.TryGetProperty("is_rereading", out var r) && r.ValueKind == JsonValueKind.True;
                if (StatusOf(Str(mine, "status"), rereading) is not { } status)
                    continue;
                items.Add(new TrackerListItem(id.ToString(CultureInfo.InvariantCulture), new TrackerEntry(
                    status,
                    Count(mine, "num_chapters_read"),
                    Count(mine, "num_volumes_read"),
                    Total(node, "num_chapters"),
                    Total(node, "num_volumes"),
                    Str(node, "status") == "finished",
                    DateOf(Str(mine, "start_date")),
                    DateOf(Str(mine, "finish_date")))));
            }
        }
        int? next = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("paging", out var paging) && paging.ValueKind == JsonValueKind.Object
            && Str(paging, "next") is { } nextUrl && Uri.TryCreate(nextUrl, UriKind.Absolute, out var nextUri))
        {
            var offsetText = System.Web.HttpUtility.ParseQueryString(nextUri.Query)["offset"];
            if (int.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out var o) && o > 0)
                next = o;
        }
        return new TrackerListPage(items, next);
    }

    /// <summary>MAL documents no revoke endpoint: nothing is sent. The user removes the app's access on myanimelist.net.</summary>
    public Task RevokeAsync(TrackerAppSettings app, TrackerTokens tokens, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The form body of a write: ONLY these fields (see the class summary); the dates only <paramref name="withDates"/>.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> WriteFormOf(TrackerEntryWrite write, bool withDates = true)
    {
        var form = new List<KeyValuePair<string, string>> { new("status", StatusWord(write.Status)) };
        if (write.Status == TrackerEntryStatus.Rereading)
            form.Add(new("is_rereading", "true"));
        if (write.ChaptersRead is { } chapters)
            form.Add(new("num_chapters_read", Math.Max(0, chapters).ToString(CultureInfo.InvariantCulture)));
        if (write.VolumesRead is { } volumes)
            form.Add(new("num_volumes_read", Math.Max(0, volumes).ToString(CultureInfo.InvariantCulture)));
        if (withDates && write.StartedOn is { } started)
            form.Add(new("start_date", started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        if (withDates && write.FinishedOn is { } finished)
            form.Add(new("finish_date", finished.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        return form;
    }

    /// <summary>MAL's list status word. A re-read is a completed entry with <c>is_rereading</c> set.</summary>
    public static string StatusWord(TrackerEntryStatus status) => status switch
    {
        TrackerEntryStatus.Reading => "reading",
        TrackerEntryStatus.Completed or TrackerEntryStatus.Rereading => "completed",
        TrackerEntryStatus.OnHold => "on_hold",
        TrackerEntryStatus.Dropped => "dropped",
        TrackerEntryStatus.PlanToRead => "plan_to_read",
        _ => throw new TrackerException(TrackerFailure.Rejected),
    };

    /// <summary>The status of MAL's word (and <c>is_rereading</c>), or null for a word MAL does not document.</summary>
    public static TrackerEntryStatus? StatusOf(string? word, bool rereading) => word switch
    {
        _ when rereading => TrackerEntryStatus.Rereading,
        "reading" => TrackerEntryStatus.Reading,
        "completed" => TrackerEntryStatus.Completed,
        "on_hold" => TrackerEntryStatus.OnHold,
        "dropped" => TrackerEntryStatus.Dropped,
        "plan_to_read" => TrackerEntryStatus.PlanToRead,
        _ => null,
    };

    /// <summary>
    /// The user's entry in a <c>GET /manga/{id}</c> answer, or null when the series is on none of the user's lists (no
    /// <c>my_list_status</c>). MAL reports 0 for an unknown total; <see cref="TrackerEntry.SeriesFinished"/> only for the publishing
    /// status <c>finished</c> (an ongoing, hiatus or discontinued series is never set Completed by the engine).
    /// </summary>
    public static TrackerEntry? EntryOf(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("my_list_status", out var mine) || mine.ValueKind != JsonValueKind.Object)
            return null;
        var rereading = mine.TryGetProperty("is_rereading", out var r) && r.ValueKind == JsonValueKind.True;
        if (StatusOf(Str(mine, "status"), rereading) is not { } status)
            return null;
        return new TrackerEntry(
            status,
            Count(mine, "num_chapters_read"),
            Count(mine, "num_volumes_read"),
            Total(root, "num_chapters"),
            Total(root, "num_volumes"),
            Str(root, "status") == "finished",
            DateOf(Str(mine, "start_date")),
            DateOf(Str(mine, "finish_date")));
    }

    /// <summary>A PKCE verifier / plain challenge: 43-128 characters of the RFC 7636 unreserved set.</summary>
    public static bool IsPkceValue(string? value) =>
        value is { Length: >= 43 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~');

    private async Task<TrackerTokens> TokenAsync(List<KeyValuePair<string, string>> form, string operation, string? refresh, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(form) };
        using var response = await SendAsync(request, operation, ct);
        if (!response.IsSuccessStatusCode)
        {
            // A refresh token that is expired, revoked or unknown answers 400 invalid_grant / 401: the user must sign in again.
            if (refresh is not null && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                throw new TrackerException(TrackerFailure.NeedsReconnect, (int)response.StatusCode);
            // A code that is wrong, used or expired (400) - or a client the admin set up wrongly (401) - needs a new sign-in, not a retry.
            if (refresh is null && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                throw new TrackerException(TrackerFailure.Rejected, (int)response.StatusCode);
            throw Failure(response);
        }
        using var json = await TrackerHttp.ReadJsonAsync(response, ct);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Str(root, "access_token") is not { Length: > 0 } access)
            throw new TrackerException(TrackerFailure.Unavailable, (int)response.StatusCode);
        var expiresAt = root.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var seconds) && seconds is > 0 and < 10L * 365 * 24 * 3600
            ? _time.GetUtcNow().AddSeconds(seconds)
            : (DateTimeOffset?)null;
        return new TrackerTokens(access, Str(root, "refresh_token") is { Length: > 0 } next ? next : refresh, expiresAt);
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string operation, CancellationToken ct) =>
        TrackerHttp.SendAsync(_httpFactory.CreateClient(TrackerHttp.MyAnimeListClient), request, _pacing.MyAnimeList, _logger,
            LogEvents.Trackers.MyAnimeListCall, Slug, operation, ct);

    /// <summary>MAL's answers: 403 is its "DoS detected" (slow down, like a 429); the rest as <see cref="TrackerHttp.Failure"/>.</summary>
    private TrackerException Failure(HttpResponseMessage response) => response.StatusCode == HttpStatusCode.Forbidden
        ? new TrackerException(TrackerFailure.RateLimited, 403, TrackerHttp.RetryAfterOf(response, _time) ?? ForbiddenRetryAfter)
        : TrackerHttp.Failure(response, _time);

    /// <summary>The wait after a "403 DoS detected" without a <c>Retry-After</c>.</summary>
    public static readonly TimeSpan ForbiddenRetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>Client credentials in the request body (the guide's "Scheme 2"): the Client ID, and the secret only when the app has one.</summary>
    private static List<KeyValuePair<string, string>> Credentials(TrackerAppSettings app)
    {
        var form = new List<KeyValuePair<string, string>> { new("client_id", app.ClientId!.Trim()) };
        if (!string.IsNullOrEmpty(app.ClientSecret))
            form.Add(new("client_secret", app.ClientSecret));
        return form;
    }

    private static string RedirectOf(Uri redirect) =>
        redirect is { IsAbsoluteUri: true } && (redirect.Scheme == Uri.UriSchemeHttps || redirect.Scheme == Uri.UriSchemeHttp)
            ? redirect.OriginalString
            : throw new TrackerException(TrackerFailure.Rejected);

    private static string MediaIdOf(string mediaId) =>
        int.TryParse(mediaId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture)
            : throw new TrackerException(TrackerFailure.Rejected);

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= 0 ? n : null;

    private static int? Total(JsonElement element, string name) => Count(element, name) is { } n && n > 0 ? n : null;

    private static DateOnly? DateOf(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
