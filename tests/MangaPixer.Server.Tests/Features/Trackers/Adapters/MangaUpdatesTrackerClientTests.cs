namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers.Adapters;

using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Trackers.Adapters;
using Xunit;

/// <summary>
/// 1.43.0 (lane A): the MangaUpdates lists adapter against fake answers (shapes from the public <c>openapi.yaml</c>; nothing recorded from
/// an account). Pins the password-once sign-in, what a list write sends, the 412 five-second delay, and the failure mapping.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MangaUpdatesTrackerClientTests : IDisposable
{
    private const string Password = "synthetic-Pa55word-value";
    private const long SeriesId = 51239621230;

    private readonly TrackerAdapterHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static TrackerConnectRequest Login(string password = Password) => new(null, null, "synthetic_reader", password);

    [Fact]
    public async Task Connect_LogsInOnce_AndKeepsOnlyTheSessionToken()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"status":"success","reason":"Successfully logged in.","context":{"session_token":"session-token-value","uid":4242}}"""));

        var tokens = await _h.MangaUpdates.ConnectAsync(new TrackerAppSettings(null, null, null), Login(), CancellationToken.None);

        Assert.Equal(new TrackerTokens("session-token-value", null, null), tokens); // no lifetime stated, no refresh token
        var seen = Assert.Single(_h.Handler.Seen);
        Assert.Equal(HttpMethod.Put, seen.Method);
        Assert.Equal("https://api.mangaupdates.com/v1/account/login", seen.Uri.ToString());
        Assert.False(seen.Headers.ContainsKey("Authorization"));
        Assert.Equal(MetadataHttp.UserAgent, seen.Headers["User-Agent"]);
        using var body = JsonDocument.Parse(seen.Body!);
        Assert.Equal(["username", "password"], body.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Null(_h.MangaUpdates.BuildAuthorizeUrl(new TrackerAppSettings(null, null, null), "s", TrackerAdapterHarness.Verifier));
        Assert.Equal(TrackerConnectKind.Password, _h.MangaUpdates.ConnectKind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TrackerFailure.Rejected)] // wrong username or password: ask again, never retry
    [InlineData(HttpStatusCode.BadRequest, TrackerFailure.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, TrackerFailure.Unavailable)]
    public async Task Connect_Failures(HttpStatusCode status, TrackerFailure expected)
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"status":"exception","reason":"Invalid credentials"}""", status));
        var ex = await Assert.ThrowsAsync<TrackerException>(() => _h.MangaUpdates.ConnectAsync(new TrackerAppSettings(null, null, null), Login(), CancellationToken.None));
        Assert.Equal(expected, ex.Failure);
    }

    [Fact]
    public async Task ThePassword_NeverReachesALogLine_AnExceptionOrAToString()
    {
        var request = Login();
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"status":"success","reason":"ok","context":{"session_token":"session-token-value","uid":1}}"""));
        var tokens = await _h.MangaUpdates.ConnectAsync(new TrackerAppSettings(null, null, null), request, CancellationToken.None);
        var failures = new List<Exception>();
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError })
        {
            _h.Answer(_ => TrackerAdapterHarness.Json("""{"status":"exception","reason":"Invalid credentials"}""", status));
            failures.Add(await Assert.ThrowsAsync<TrackerException>(() =>
                _h.MangaUpdates.ConnectAsync(new TrackerAppSettings(null, null, null), request, CancellationToken.None)));
        }
        _h.Answer(_ => throw new HttpRequestException("synthetic network failure"));
        failures.Add(await Assert.ThrowsAsync<TrackerException>(() =>
            _h.MangaUpdates.ConnectAsync(new TrackerAppSettings(null, null, null), request, CancellationToken.None)));

        Assert.NotEmpty(_h.Logs.Lines);
        var text = string.Join('\n', _h.AllLogs, string.Join('\n', failures), request, tokens);
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain("session-token-value", text, StringComparison.Ordinal);
        Assert.Equal("TrackerConnectRequest { [redacted] }", request.ToString());
        Assert.Contains("AccessToken = [redacted]", tokens.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_SendsNothing_TheSessionTokenIsKept()
    {
        var tokens = await _h.MangaUpdates.RefreshAsync(new TrackerAppSettings(null, null, null), TrackerAdapterHarness.Tokens, CancellationToken.None);
        Assert.Equal(TrackerAdapterHarness.Tokens, tokens);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task WhoAmI_ReadsTheProfile()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"user_id":4242,"username":"synthetic_reader","url":"https://www.mangaupdates.com/member/x"}"""));
        Assert.Equal(new TrackerIdentity("4242", "synthetic_reader"), await _h.MangaUpdates.WhoAmIAsync(TrackerAdapterHarness.Tokens, CancellationToken.None));
        var seen = Assert.Single(_h.Handler.Seen);
        Assert.Equal("https://api.mangaupdates.com/v1/account/profile", seen.Uri.ToString());
        Assert.Equal("Bearer " + TrackerAdapterHarness.Access, seen.Headers["Authorization"]);
    }

    [Fact]
    public async Task ReadEntry_OnNoList_IsNull()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"status":"exception","reason":"List or series not found"}""", HttpStatusCode.NotFound));
        Assert.Null(await _h.MangaUpdates.ReadEntryAsync(TrackerAdapterHarness.Tokens, SeriesId.ToString(), CancellationToken.None));
        Assert.Equal($"https://api.mangaupdates.com/v1/lists/series/{SeriesId}", Assert.Single(_h.Handler.Seen).Uri.ToString());
    }

    [Theory]
    [InlineData("read", 0, TrackerEntryStatus.Reading)]
    [InlineData("wish", 1, TrackerEntryStatus.PlanToRead)]
    [InlineData("complete", 2, TrackerEntryStatus.Completed)]
    [InlineData("unfinished", 3, TrackerEntryStatus.Dropped)]
    [InlineData("hold", 4, TrackerEntryStatus.OnHold)]
    [InlineData("hold", 101, TrackerEntryStatus.OnHold)] // a custom list of type "hold"
    [InlineData(null, 2, TrackerEntryStatus.Completed)] // no type: the standard list id
    public async Task ReadEntry_MapsTheList_AndHasNoTotals(string? type, int listId, TrackerEntryStatus expected)
    {
        var typeJson = type is null ? "" : $",\"list_type\":\"{type}\"";
        _h.Answer(_ => TrackerAdapterHarness.Json($$"""{"series":{"id":{{SeriesId}},"title":"Synthetic"},"list_id":{{listId}}{{typeJson}},"status":{"volume":4,"chapter":37},"priority":0}"""));

        var entry = await _h.MangaUpdates.ReadEntryAsync(TrackerAdapterHarness.Tokens, SeriesId.ToString(), CancellationToken.None);

        Assert.Equal(new TrackerEntry(expected, 37, 4, null, null, false, null, null), entry);
    }

    [Fact]
    public async Task Write_AddsASeriesThatIsOnNoList_WithOnlyIdListAndNumbers()
    {
        _h.Answer(r => r.Method == HttpMethod.Get
            ? TrackerAdapterHarness.Json("{}", HttpStatusCode.NotFound)
            : TrackerAdapterHarness.Json("""{"status":"success","reason":"Series added"}"""));

        await _h.MangaUpdates.WriteEntryAsync(TrackerAdapterHarness.Tokens, SeriesId.ToString(),
            new TrackerEntryWrite(TrackerEntryStatus.Reading, 12, 2, new DateOnly(2026, 1, 2), new DateOnly(2026, 2, 3), Private: true), CancellationToken.None);

        Assert.Equal(2, _h.Handler.CallCount);
        var add = _h.Handler.Seen[1];
        Assert.Equal(HttpMethod.Post, add.Method);
        Assert.Equal("https://api.mangaupdates.com/v1/lists/series", add.Uri.ToString());
        Assert.Equal("Bearer " + TrackerAdapterHarness.Access, add.Headers["Authorization"]);
        Assert.Equal($$"""[{"series":{"id":{{SeriesId}}},"list_id":0,"status":{"volume":2,"chapter":12}}]""", add.Body);
    }

    [Fact]
    public async Task Write_UpdatesAnEntry_KeepingTheUsersOwnListOfTheSameType()
    {
        _h.Answer(r => r.Method == HttpMethod.Get
            ? TrackerAdapterHarness.Json($$"""{"series":{"id":{{SeriesId}}},"list_id":101,"list_type":"read","status":{"volume":1,"chapter":5}}""")
            : TrackerAdapterHarness.Json("""{"status":"success","reason":"Updated"}"""));

        await _h.MangaUpdates.WriteEntryAsync(TrackerAdapterHarness.Tokens, SeriesId.ToString(),
            new TrackerEntryWrite(TrackerEntryStatus.Reading, 9, null, null, null, null), CancellationToken.None);
        await _h.MangaUpdates.WriteEntryAsync(TrackerAdapterHarness.Tokens, SeriesId.ToString(),
            new TrackerEntryWrite(TrackerEntryStatus.Completed, 40, 4, null, null, null), CancellationToken.None);

        var writes = _h.Handler.Seen.Where(s => s.Method == HttpMethod.Post).ToList();
        Assert.All(writes, w => Assert.Equal("https://api.mangaupdates.com/v1/lists/series/update", w.Uri.ToString()));
        Assert.Equal($$"""[{"series":{"id":{{SeriesId}}},"list_id":101,"status":{"chapter":9}}]""", writes[0].Body);
        Assert.Equal($$"""[{"series":{"id":{{SeriesId}}},"list_id":2,"status":{"volume":4,"chapter":40}}]""", writes[1].Body);
    }

    [Fact]
    public async Task TheFiveSecondUpdateDelay_IsRateLimitedForFiveSeconds()
    {
        _h.Answer(r => r.Method == HttpMethod.Get
            ? TrackerAdapterHarness.Json("{}", HttpStatusCode.NotFound)
            : TrackerAdapterHarness.Json("""{"status":"exception","reason":"Five second update delay"}""", HttpStatusCode.PreconditionFailed));

        var ex = await Assert.ThrowsAsync<TrackerException>(() => _h.MangaUpdates.WriteEntryAsync(TrackerAdapterHarness.Tokens, SeriesId.ToString(),
            new TrackerEntryWrite(TrackerEntryStatus.Reading, 1, null, null, null, null), CancellationToken.None));

        Assert.Equal(TrackerFailure.RateLimited, ex.Failure);
        Assert.Equal(412, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), ex.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TrackerFailure.NeedsReconnect)]
    [InlineData(HttpStatusCode.TooManyRequests, TrackerFailure.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, TrackerFailure.Rejected)]
    [InlineData(HttpStatusCode.BadGateway, TrackerFailure.Unavailable)]
    public async Task FailedCalls_MapOntoTheSeamFailures(HttpStatusCode status, TrackerFailure expected)
    {
        _h.Answer(_ => TrackerAdapterHarness.Status(status));
        var ex = await Assert.ThrowsAsync<TrackerException>(() => _h.MangaUpdates.ReadEntryAsync(TrackerAdapterHarness.Tokens, SeriesId.ToString(), CancellationToken.None));
        Assert.Equal(expected, ex.Failure);
        Assert.Equal((int)status, ex.StatusCode);
    }

    [Fact]
    public async Task ANetworkFailure_IsUnavailable_WithoutTheInnerText()
    {
        _h.Answer(_ => throw new HttpRequestException("synthetic inner text"));
        var ex = await Assert.ThrowsAsync<TrackerException>(() => _h.MangaUpdates.WhoAmIAsync(TrackerAdapterHarness.Tokens, CancellationToken.None));
        Assert.Equal(TrackerFailure.Unavailable, ex.Failure);
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain("synthetic inner text", _h.AllLogs + ex, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)] // already signed out
    public async Task Revoke_LogsOut(HttpStatusCode status)
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"status":"success","reason":"ok"}""", status));
        await _h.MangaUpdates.RevokeAsync(new TrackerAppSettings(null, null, null), TrackerAdapterHarness.Tokens, CancellationToken.None);
        var seen = Assert.Single(_h.Handler.Seen);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("https://api.mangaupdates.com/v1/account/logout", seen.Uri.ToString());
        Assert.Equal("Bearer " + TrackerAdapterHarness.Access, seen.Headers["Authorization"]);
    }

    [Fact]
    public async Task ARedirect_IsRefused_AndNotFollowed()
    {
        _h.Answer(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            redirect.Headers.Location = new Uri("https://elsewhere.example/v1/account/login");
            return redirect;
        });
        var ex = await Assert.ThrowsAsync<TrackerException>(() => _h.MangaUpdates.ConnectAsync(new TrackerAppSettings(null, null, null), Login(), CancellationToken.None));
        Assert.Equal(TrackerFailure.Rejected, ex.Failure);
        Assert.Null(ex.StatusCode);
        Assert.Single(_h.Handler.Seen);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("njeqwry")] // a base36 site slug, not the API id
    public async Task AnInvalidSeriesId_IsRejected_WithoutARequest(string mediaId)
    {
        var ex = await Assert.ThrowsAsync<TrackerException>(() => _h.MangaUpdates.ReadEntryAsync(TrackerAdapterHarness.Tokens, mediaId, CancellationToken.None));
        Assert.Equal(TrackerFailure.Rejected, ex.Failure);
        Assert.Equal(0, _h.Handler.CallCount);
    }
}
