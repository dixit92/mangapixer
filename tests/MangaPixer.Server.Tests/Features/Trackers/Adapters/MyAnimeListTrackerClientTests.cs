namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers.Adapters;

using System.Net;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Trackers.Adapters;
using Xunit;

/// <summary>
/// 1.43.0 (lane A): the MyAnimeList adapter against fake answers (shapes from MAL's public API reference and authorization guide; nothing
/// recorded from an account). Pins what is sent (the authorize URL, the token form, the write form's exact fields), how answers map onto
/// the seam types, and how failures map onto <see cref="TrackerFailure"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MyAnimeListTrackerClientTests : IDisposable
{
    private readonly TrackerAdapterHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void TheAuthorizeUrl_IsMalsOwn_WithPlainPkce_AndTheRegisteredRedirect()
    {
        var url = _h.Mal.BuildAuthorizeUrl(TrackerAdapterHarness.App(), "state-123", TrackerAdapterHarness.Verifier)!;

        Assert.Equal("https://myanimelist.net/v1/oauth2/authorize", url.GetLeftPart(UriPartial.Path));
        var query = TrackerAdapterHarness.Form(url.Query.TrimStart('?'));
        Assert.Equal(
            ["response_type", "client_id", "state", "redirect_uri", "code_challenge", "code_challenge_method"],
            query.Select(p => p.Key).ToArray());
        Assert.Equal("code", query.Single(p => p.Key == "response_type").Value);
        Assert.Equal("synthetic-client-id", query.Single(p => p.Key == "client_id").Value);
        Assert.Equal("state-123", query.Single(p => p.Key == "state").Value);
        Assert.Equal("https://reader.example/trackers/callback/mal", query.Single(p => p.Key == "redirect_uri").Value);
        Assert.Equal(TrackerAdapterHarness.Verifier, query.Single(p => p.Key == "code_challenge").Value);
        Assert.Equal("plain", query.Single(p => p.Key == "code_challenge_method").Value);
        Assert.Equal(0, _h.Handler.CallCount); // building a URL sends nothing
        Assert.Equal(TrackerConnectKind.OAuthPkce, _h.Mal.ConnectKind);
        Assert.Equal(TrackerSlugs.MyAnimeList, _h.Mal.Slug);
    }

    [Theory]
    [InlineData(null, "state", TrackerAdapterHarness.Verifier)] // no app registered
    [InlineData("id", "", TrackerAdapterHarness.Verifier)] // no state
    [InlineData("id", "state", "too-short")] // PKCE needs 43-128 characters
    [InlineData("id", "state", "has a space and is long enough to pass the length check of pkce")]
    public void TheAuthorizeUrl_RefusesAnIncompleteSignIn(string? clientId, string state, string challenge)
    {
        var ex = Assert.Throws<TrackerException>(() => _h.Mal.BuildAuthorizeUrl(new TrackerAppSettings(clientId, null, null), state, challenge));
        Assert.Equal(TrackerFailure.Rejected, ex.Failure);
    }

    [Fact]
    public async Task Connect_ExchangesTheCode_WithTheVerifierAndTheRedirect_AndReadsExpiresIn()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"token_type":"Bearer","expires_in":2415600,"access_token":"new-access","refresh_token":"new-refresh"}"""));

        var tokens = await _h.Mal.ConnectAsync(TrackerAdapterHarness.App(secret: "synthetic-secret"),
            new TrackerConnectRequest("the-code", TrackerAdapterHarness.Verifier, null, null), CancellationToken.None);

        Assert.Equal("new-access", tokens.AccessToken);
        Assert.Equal("new-refresh", tokens.RefreshToken);
        Assert.Equal(_h.Time.GetUtcNow().AddSeconds(2415600), tokens.ExpiresAt);
        var seen = Assert.Single(_h.Handler.Seen);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("https://myanimelist.net/v1/oauth2/token", seen.Uri.ToString());
        Assert.False(seen.Headers.ContainsKey("Authorization")); // client credentials travel in the body (the guide's scheme 2)
        Assert.Equal(MetadataHttp.UserAgent, seen.Headers["User-Agent"]);
        var form = TrackerAdapterHarness.Form(seen.Body);
        Assert.Equal(["client_id", "client_secret", "grant_type", "code", "code_verifier", "redirect_uri"], form.Select(p => p.Key).ToArray());
        Assert.Equal("authorization_code", form.Single(p => p.Key == "grant_type").Value);
        Assert.Equal(TrackerAdapterHarness.Verifier, form.Single(p => p.Key == "code_verifier").Value);
    }

    [Fact]
    public async Task Connect_WithoutASecret_SendsNoSecretField()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"expires_in":3600,"access_token":"a","refresh_token":"r"}"""));
        await _h.Mal.ConnectAsync(TrackerAdapterHarness.App(), new TrackerConnectRequest("c", TrackerAdapterHarness.Verifier, null, null), CancellationToken.None);
        Assert.DoesNotContain(TrackerAdapterHarness.Form(_h.Handler.Seen[0].Body), p => p.Key == "client_secret");
    }

    [Fact]
    public async Task Connect_WithABadCode_IsRejected_NotAReconnect()
    {
        _h.Answer(_ => TrackerAdapterHarness.Status(HttpStatusCode.BadRequest));
        var ex = await Assert.ThrowsAsync<TrackerException>(() => _h.Mal.ConnectAsync(TrackerAdapterHarness.App(),
            new TrackerConnectRequest("used-code", TrackerAdapterHarness.Verifier, null, null), CancellationToken.None));
        Assert.Equal(TrackerFailure.Rejected, ex.Failure);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task Refresh_ReturnsTheNewRefreshToken()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"token_type":"Bearer","expires_in":3600,"access_token":"access-2","refresh_token":"refresh-2"}"""));

        var tokens = await _h.Mal.RefreshAsync(TrackerAdapterHarness.App(), TrackerAdapterHarness.Tokens, CancellationToken.None);

        Assert.Equal("access-2", tokens.AccessToken);
        Assert.Equal("refresh-2", tokens.RefreshToken);
        Assert.Equal(_h.Time.GetUtcNow().AddHours(1), tokens.ExpiresAt);
        var form = TrackerAdapterHarness.Form(Assert.Single(_h.Handler.Seen).Body);
        Assert.Equal(["client_id", "grant_type", "refresh_token"], form.Select(p => p.Key).ToArray());
        Assert.Equal(TrackerAdapterHarness.Refresh, form.Single(p => p.Key == "refresh_token").Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)] // invalid_grant: expired / revoked refresh token
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Refresh_ThatIsRefused_NeedsAReconnect(HttpStatusCode status)
    {
        _h.Answer(_ => TrackerAdapterHarness.Status(status));
        var ex = await Assert.ThrowsAsync<TrackerException>(() => _h.Mal.RefreshAsync(TrackerAdapterHarness.App(), TrackerAdapterHarness.Tokens, CancellationToken.None));
        Assert.Equal(TrackerFailure.NeedsReconnect, ex.Failure);
    }

    [Fact]
    public async Task Refresh_WithoutARefreshToken_NeedsAReconnect_AndSendsNothing()
    {
        var ex = await Assert.ThrowsAsync<TrackerException>(() =>
            _h.Mal.RefreshAsync(TrackerAdapterHarness.App(), new TrackerTokens("a", null, null), CancellationToken.None));
        Assert.Equal(TrackerFailure.NeedsReconnect, ex.Failure);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task WhoAmI_ReadsMe_WithTheBearerToken()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"id":4242,"name":"synthetic_reader","joined_at":"2020-01-01T00:00:00+00:00"}"""));

        var me = await _h.Mal.WhoAmIAsync(TrackerAdapterHarness.Tokens, CancellationToken.None);

        Assert.Equal(new TrackerIdentity("4242", "synthetic_reader"), me);
        var seen = Assert.Single(_h.Handler.Seen);
        Assert.Equal("https://api.myanimelist.net/v2/users/@me", seen.Uri.ToString());
        Assert.Equal("Bearer " + TrackerAdapterHarness.Access, seen.Headers["Authorization"]);
    }

    [Fact]
    public async Task ReadEntry_MapsTheListStatus_TheTotals_AndTheDates()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""
            {"id":2,"title":"Synthetic","num_chapters":0,"num_volumes":12,"status":"finished",
             "my_list_status":{"status":"on_hold","is_rereading":false,"num_volumes_read":3,"num_chapters_read":31,"score":8,
                               "start_date":"2024-02-03","finish_date":"2024-13-40","updated_at":"2024-02-03T00:00:00+00:00"}}
            """));

        var entry = await _h.Mal.ReadEntryAsync(TrackerAdapterHarness.Tokens, "2", CancellationToken.None);

        Assert.Equal(new TrackerEntry(TrackerEntryStatus.OnHold, 31, 3, null, 12, true, new DateOnly(2024, 2, 3), null), entry);
        var seen = Assert.Single(_h.Handler.Seen);
        Assert.Equal("https://api.myanimelist.net/v2/manga/2?fields=num_chapters,num_volumes,status,my_list_status", seen.Uri.ToString());
        Assert.Equal(HttpMethod.Get, seen.Method);
    }

    [Theory]
    [InlineData("""{"id":2,"num_chapters":100,"status":"currently_publishing"}""", null)] // not on the user's list
    [InlineData("""{"id":2,"num_chapters":100,"status":"currently_publishing","my_list_status":{"status":"completed","is_rereading":true,"num_chapters_read":100}}""", TrackerEntryStatus.Rereading)]
    [InlineData("""{"id":2,"status":"on_hiatus","my_list_status":{"status":"plan_to_read"}}""", TrackerEntryStatus.PlanToRead)]
    [InlineData("""{"id":2,"status":"finished","my_list_status":{"status":"reading"}}""", TrackerEntryStatus.Reading)]
    [InlineData("""{"id":2,"status":"finished","my_list_status":{"status":"dropped"}}""", TrackerEntryStatus.Dropped)]
    public async Task ReadEntry_MapsEveryMalStatus(string json, TrackerEntryStatus? expected)
    {
        _h.Answer(_ => TrackerAdapterHarness.Json(json));
        var entry = await _h.Mal.ReadEntryAsync(TrackerAdapterHarness.Tokens, "2", CancellationToken.None);
        Assert.Equal(expected, entry?.Status);
        if (entry is not null && !json.Contains("\"finished\"", StringComparison.Ordinal))
            Assert.False(entry.SeriesFinished); // a running or paused series is never "finished"
    }

    [Fact]
    public async Task Write_SendsExactlyTheAllowedFields()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"status":"reading","num_chapters_read":12,"num_volumes_read":2}"""));

        await _h.Mal.WriteEntryAsync(TrackerAdapterHarness.Tokens, "2",
            new TrackerEntryWrite(TrackerEntryStatus.Reading, 12, 2, new DateOnly(2026, 1, 2), new DateOnly(2026, 3, 4), Private: true),
            CancellationToken.None);

        var seen = Assert.Single(_h.Handler.Seen);
        Assert.Equal(HttpMethod.Patch, seen.Method);
        Assert.Equal("https://api.myanimelist.net/v2/manga/2/my_list_status", seen.Uri.ToString());
        Assert.Equal("Bearer " + TrackerAdapterHarness.Access, seen.Headers["Authorization"]);
        var form = TrackerAdapterHarness.Form(seen.Body);
        // 1.43.0 (owner A1): the start / finish dates go with the write; still no private flag, score or anything else.
        Assert.Equal(["status", "num_chapters_read", "num_volumes_read", "start_date", "finish_date"], form.Select(p => p.Key).ToArray());
        Assert.Equal(["reading", "12", "2", "2026-01-02", "2026-03-04"], form.Select(p => p.Value).ToArray());
    }

    [Theory]
    [InlineData(TrackerEntryStatus.Completed, "completed", false)]
    [InlineData(TrackerEntryStatus.OnHold, "on_hold", false)]
    [InlineData(TrackerEntryStatus.Dropped, "dropped", false)]
    [InlineData(TrackerEntryStatus.PlanToRead, "plan_to_read", false)]
    [InlineData(TrackerEntryStatus.Rereading, "completed", true)]
    public void TheWriteForm_MapsEveryStatus_AndLeavesOutUnsetNumbers(TrackerEntryStatus status, string word, bool rereading)
    {
        var form = MyAnimeListTrackerClient.WriteFormOf(new TrackerEntryWrite(status, null, null, null, null, null));
        Assert.Equal(word, form.Single(p => p.Key == "status").Value);
        Assert.Equal(rereading, form.Any(p => p.Key == "is_rereading" && p.Value == "true"));
        Assert.Equal(rereading ? 2 : 1, form.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TrackerFailure.NeedsReconnect)]
    [InlineData(HttpStatusCode.Forbidden, TrackerFailure.RateLimited)] // "DoS detected"
    [InlineData(HttpStatusCode.TooManyRequests, TrackerFailure.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, TrackerFailure.Rejected)]
    [InlineData(HttpStatusCode.BadRequest, TrackerFailure.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, TrackerFailure.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, TrackerFailure.Unavailable)]
    public async Task FailedCalls_MapOntoTheSeamFailures(HttpStatusCode status, TrackerFailure expected)
    {
        _h.Answer(_ => TrackerAdapterHarness.Status(status));
        var read = await Assert.ThrowsAsync<TrackerException>(() => _h.Mal.ReadEntryAsync(TrackerAdapterHarness.Tokens, "2", CancellationToken.None));
        var write = await Assert.ThrowsAsync<TrackerException>(() => _h.Mal.WriteEntryAsync(TrackerAdapterHarness.Tokens, "2",
            new TrackerEntryWrite(TrackerEntryStatus.Reading, 1, null, null, null, null), CancellationToken.None));
        Assert.Equal(expected, read.Failure);
        Assert.Equal(expected, write.Failure);
        Assert.Equal((int)status, read.StatusCode);
        Assert.Equal($"Tracker call failed: {expected} (HTTP {(int)status}).", read.Message);
        Assert.Null(read.InnerException);
    }

    [Fact]
    public async Task A401_NeedsAReconnect()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"error":"invalid_token"}""", HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<TrackerException>(() => _h.Mal.WhoAmIAsync(TrackerAdapterHarness.Tokens, CancellationToken.None));
        Assert.Equal(TrackerFailure.NeedsReconnect, ex.Failure);
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task RateLimits_HonourRetryAfter()
    {
        _h.Answer(_ => TrackerAdapterHarness.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(30)));
        var ex = await Assert.ThrowsAsync<TrackerException>(() => _h.Mal.ReadEntryAsync(TrackerAdapterHarness.Tokens, "2", CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);

        _h.Answer(_ => TrackerAdapterHarness.Status(HttpStatusCode.Forbidden));
        ex = await Assert.ThrowsAsync<TrackerException>(() => _h.Mal.ReadEntryAsync(TrackerAdapterHarness.Tokens, "2", CancellationToken.None));
        Assert.Equal(MyAnimeListTrackerClient.ForbiddenRetryAfter, ex.RetryAfter);
    }

    [Fact]
    public async Task ARedirect_IsRefused_AndNotFollowed()
    {
        _h.Answer(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://elsewhere.example/v2/users/@me");
            return redirect;
        });

        var ex = await Assert.ThrowsAsync<TrackerException>(() => _h.Mal.WhoAmIAsync(TrackerAdapterHarness.Tokens, CancellationToken.None));

        Assert.Equal(TrackerFailure.Rejected, ex.Failure);
        Assert.Null(ex.StatusCode); // refused by the allowlist handler, not mapped from a status
        Assert.Contains("redirect_refused", _h.AllLogs, StringComparison.Ordinal);
        Assert.Equal(TrackerHttp.MyAnimeListApiHost, Assert.Single(_h.Handler.Seen).Uri.Host);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("2/../../users/@me")]
    public async Task AnInvalidMediaId_IsRejected_WithoutARequest(string mediaId)
    {
        var ex = await Assert.ThrowsAsync<TrackerException>(() => _h.Mal.ReadEntryAsync(TrackerAdapterHarness.Tokens, mediaId, CancellationToken.None));
        Assert.Equal(TrackerFailure.Rejected, ex.Failure);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task Revoke_SendsNothing_MalHasNoRevokeEndpoint()
    {
        await _h.Mal.RevokeAsync(TrackerAdapterHarness.App(), TrackerAdapterHarness.Tokens, CancellationToken.None);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task NoTokenOrSecret_ReachesALogLine_OrAnException()
    {
        var app = TrackerAdapterHarness.App(secret: "synthetic-secret-value");
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"expires_in":3600,"access_token":"fresh-access-value","refresh_token":"fresh-refresh-value"}"""));
        var tokens = await _h.Mal.ConnectAsync(app, new TrackerConnectRequest("one-time-code-value", TrackerAdapterHarness.Verifier, null, null), CancellationToken.None);
        _h.Answer(_ => TrackerAdapterHarness.Status(HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<TrackerException>(() => _h.Mal.ReadEntryAsync(tokens, "2", CancellationToken.None));

        var text = _h.AllLogs + ex + tokens + app;
        foreach (var secret in new[] { "synthetic-secret-value", "fresh-access-value", "fresh-refresh-value", "one-time-code-value", TrackerAdapterHarness.Verifier })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.Contains("[redacted]", tokens.ToString(), StringComparison.Ordinal);
    }
}
