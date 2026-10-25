namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests of personal access tokens (1.33.0) through the real pipeline: the export policy accepts an admin cookie or a
/// valid token, a token reaches nothing outside <c>/api/v1/export</c>, the two rate limits, the admin API and the secret's
/// absence from logs and list responses.
/// </summary>
public sealed class ApiTokenHttpTests
{
    private const string Ping = "/api/v1/export/ping";

    [Fact]
    public async Task Ping_WithoutCredentials_Is401_WithABearerChallenge()
    {
        await using var factory = new ApiTokenTestFactory();
        await factory.AdminAsync();

        var response = await factory.CreateClient().GetAsync(Ping);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Fact]
    public async Task Ping_WithAValidToken_Is200_AsToken_ForGetAndHead()
    {
        await using var factory = new ApiTokenTestFactory();
        var created = await factory.CreateTokenAsync();
        var client = factory.BearerClient(created.Secret);

        var response = await client.GetAsync(Ping);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportPingDto>(TestJson.Web);
        Assert.True(body!.Ok);
        Assert.Equal("token", body.Auth);

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Ping));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);

        // The lowercase scheme name is accepted too (RFC 7235: case-insensitive).
        var lower = factory.CreateClient();
        lower.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "bearer " + created.Secret);
        Assert.Equal(HttpStatusCode.OK, (await lower.GetAsync(Ping)).StatusCode);

        var listed = Assert.Single(await (await factory.AdminAsync()).GetFromJsonAsync<List<ApiTokenDto>>("/api/v1/admin/tokens", TestJson.Web) ?? []);
        Assert.NotNull(listed.LastUsedAt);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("mpx_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("")]
    public async Task Ping_WithABadToken_Is401(string value)
    {
        await using var factory = new ApiTokenTestFactory();
        await factory.AdminAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + value);

        var response = await client.GetAsync(Ping);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ping_WithARevokedOrExpiredToken_Is401()
    {
        await using var factory = new ApiTokenTestFactory();
        var admin = await factory.AdminAsync();
        var revoked = await factory.CreateTokenAsync("revoked");
        var expired = await factory.CreateTokenAsync("expired", 30);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/admin/tokens/{revoked.Token.Id}/revoke", null)).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var past = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.ApiTokens.Where(t => t.PublicId == expired.Token.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, past));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.BearerClient(revoked.Secret).GetAsync(Ping)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.BearerClient(expired.Secret).GetAsync(Ping)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/admin/tokens/nope/revoke", null)).StatusCode);
    }

    [Fact]
    public async Task Ping_AfterTheOwnerIsDemotedOrDeleted_Is401()
    {
        await using var factory = new ApiTokenTestFactory();
        var admin = await factory.AdminAsync();
        await factory.CreateUserAsync("second", "SecondPassword123!", isAdmin: true);
        var second = await factory.LoginAsync("second", "SecondPassword123!");
        var demoted = await factory.CreateTokenAsync("demoted", client: second);
        Assert.Equal(HttpStatusCode.OK, (await factory.BearerClient(demoted.Secret).GetAsync(Ping)).StatusCode);

        var users = await admin.GetFromJsonAsync<List<AdminUserDto>>("/api/v1/admin/users", TestJson.Web);
        var secondId = users!.Single(u => u.Username == "second").Id;
        (await admin.PostAsJsonAsync($"/api/v1/admin/users/{secondId}/update", new UpdateUserRequest { IsAdmin = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.BearerClient(demoted.Secret).GetAsync(Ping)).StatusCode);

        (await admin.PostAsJsonAsync($"/api/v1/admin/users/{secondId}/update", new UpdateUserRequest { IsAdmin = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await factory.BearerClient(demoted.Secret).GetAsync(Ping)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/admin/users/{secondId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.BearerClient(demoted.Secret).GetAsync(Ping)).StatusCode);
        Assert.Empty(await admin.GetFromJsonAsync<List<ApiTokenDto>>("/api/v1/admin/tokens", TestJson.Web) ?? []);
    }

    [Fact]
    public async Task Ping_WithAReadersCookie_Is403_AndWithAnAdminsCookie_Is200AsCookie()
    {
        await using var factory = new ApiTokenTestFactory();
        var admin = await factory.AdminAsync();
        await factory.CreateUserAsync("reader", "ReaderPassword123!", isAdmin: false);
        var reader = await factory.LoginAsync("reader", "ReaderPassword123!");

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(Ping)).StatusCode);

        var ok = await admin.GetAsync(Ping);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("cookie", (await ok.Content.ReadFromJsonAsync<ExportPingDto>(TestJson.Web))!.Auth);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task UnsafeMethods_WithAValidToken_AreRefused_ByTheScheme_AndOverHttp(string method)
    {
        await using var factory = new ApiTokenTestFactory();
        var created = await factory.CreateTokenAsync();

        // Over HTTP: exactly what the same request gets without credentials (no export endpoint takes a write).
        var anonymous = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), Ping));
        var withToken = await factory.BearerClient(created.Secret).SendAsync(new HttpRequestMessage(new HttpMethod(method), Ping));
        Assert.Equal(anonymous.StatusCode, withToken.StatusCode);
        Assert.False(withToken.IsSuccessStatusCode);

        // The scheme itself refuses a non-GET request carrying a valid token.
        using var scope = factory.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = method;
        context.Request.Path = Ping;
        context.Request.Headers.Authorization = "Bearer " + created.Secret;
        var result = await context.AuthenticateAsync(ExportApi.TokenScheme);
        Assert.False(result.Succeeded);
        Assert.Equal("method_not_allowed", result.Failure?.Message);

        // A request scope of its own: the scoped handler provider caches a scheme's handler (and its result) per request.
        using var getScope = factory.Services.CreateScope();
        var getContext = new DefaultHttpContext { RequestServices = getScope.ServiceProvider };
        getContext.Request.Method = "GET";
        getContext.Request.Path = Ping;
        getContext.Request.Headers.Authorization = "Bearer " + created.Secret;
        var get = await getContext.AuthenticateAsync(ExportApi.TokenScheme);
        Assert.True(get.Succeeded, get.Failure?.Message ?? "no result");
        Assert.False(get.Principal!.IsInRole("admin"));
        Assert.Null(get.Principal.Identity!.Name);
    }

    /// <summary>
    /// The sweep: for EVERY endpoint outside <c>/api/v1/export</c>, every method it maps, a request with a valid token and no
    /// cookie gets exactly the status the same request gets with no credentials. A token unlocks nothing else.
    /// </summary>
    [Fact]
    public async Task Sweep_AValidToken_UnlocksNoEndpointOutsideTheExport()
    {
        await using var factory = new ApiTokenTestFactory();
        var created = await factory.CreateTokenAsync();
        var anonymous = factory.CreateClient();
        var bearer = factory.BearerClient(created.Secret);

        var endpoints = AccessSweepSupport.Endpoints(factory.Services);
        var checkedRequests = 0;
        var mismatches = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var url = "/" + AccessSweepSupport.Concrete(endpoint.RoutePattern);
            if (url.StartsWith("/" + ExportApi.RoutePrefix + "/", StringComparison.OrdinalIgnoreCase))
                continue;
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var method in methods)
            {
                var expected = await anonymous.SendAsync(AccessSweepSupport.Request(method, url));
                var actual = await bearer.SendAsync(AccessSweepSupport.Request(method, url));
                checkedRequests++;
                if (expected.StatusCode != actual.StatusCode)
                    mismatches.Add($"{method} {url}: {(int)expected.StatusCode} without credentials, {(int)actual.StatusCode} with a token");
            }
        }

        Assert.True(checkedRequests > 100, $"only {checkedRequests} requests were swept");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
        // The admin API in particular: a token never lists, creates or revokes tokens.
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/v1/admin/tokens")).StatusCode);
    }

    /// <summary>The ONE unsafe method a token may use (1.36.0, owner-approved): method + route template.</summary>
    private static readonly string[] TokenWriteAllowlist = ["POST api/v1/export/libraries/{id}/scan"];

    /// <summary>
    /// The write sweep (1.36.0): (1) every unsafe method mapped under <c>/api/v1/export</c> is in <see cref="TokenWriteAllowlist"/>,
    /// which holds exactly the scan request, and only that endpoint carries the token-write marker, the antiforgery opt-out of the
    /// export and the scan policy; (2) the two export policies are named only under the export prefix; (3) over HTTP, a valid token
    /// holding EVERY scope gets, for every other mapped (method, endpoint) - unsafe ones everywhere, and every method outside the
    /// export - exactly the status the same request gets with no credentials.
    /// </summary>
    [Fact]
    public async Task Sweep_OnlyTheScanRequest_AcceptsAnUnsafeMethodFromAToken()
    {
        await using var factory = new ApiTokenTestFactory();
        var created = await factory.CreateTokenAsync(scopes: ExportApi.KnownScopes);
        Assert.Equal(ExportApi.KnownScopes, created.Token.Scopes);
        var anonymous = factory.CreateClient();
        var bearer = factory.BearerClient(created.Secret);

        var endpoints = AccessSweepSupport.Endpoints(factory.Services);
        static bool IsSafe(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        static string Key(string method, RouteEndpoint e) => method + " " + e.RoutePattern.RawText?.TrimStart('/');
        static bool UnderExport(RouteEndpoint e) =>
            (e.RoutePattern.RawText?.TrimStart('/') ?? string.Empty).StartsWith(ExportApi.RoutePrefix + "/", StringComparison.OrdinalIgnoreCase);
        static IEnumerable<string> MethodsOf(RouteEndpoint e) => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
        static IEnumerable<string?> PoliciesOf(RouteEndpoint e) => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy);

        // (1) The export's unsafe methods, the marker, the antiforgery opt-out and the scan policy: the allowlisted route only.
        var exportWrites = endpoints.Where(UnderExport).SelectMany(e => MethodsOf(e).Where(m => !IsSafe(m)).Select(m => Key(m, e))).Order().ToList();
        Assert.Equal(TokenWriteAllowlist, exportWrites);
        var marked = endpoints.Where(e => e.Metadata.GetMetadata<TokenWriteAllowedAttribute>() is not null)
            .SelectMany(e => MethodsOf(e).Select(m => Key(m, e))).ToList();
        Assert.Equal(TokenWriteAllowlist, marked);
        var exportCsrfOptOuts = endpoints.Where(e => UnderExport(e) && e.Metadata.GetMetadata<IgnoreAntiforgeryTokenAttribute>() is not null)
            .SelectMany(e => MethodsOf(e).Select(m => Key(m, e))).ToList();
        Assert.Equal(TokenWriteAllowlist, exportCsrfOptOuts);
        var scanPolicy = endpoints.Where(e => PoliciesOf(e).Contains(ExportApi.ScanPolicy)).SelectMany(e => MethodsOf(e).Select(m => Key(m, e))).ToList();
        Assert.Equal(TokenWriteAllowlist, scanPolicy);
        var scanEndpoint = endpoints.Single(e => e.Metadata.GetMetadata<TokenWriteAllowedAttribute>() is not null);
        Assert.All(PoliciesOf(scanEndpoint), p => Assert.Equal(ExportApi.ScanPolicy, p));

        // (2) The policies that name the token scheme guard export routes only.
        var outside = endpoints.Where(e => !UnderExport(e) && PoliciesOf(e).Any(p => p is ExportApi.Policy or ExportApi.ScanPolicy))
            .Select(e => e.RoutePattern.RawText).ToList();
        Assert.True(outside.Count == 0, "export policy outside the export: " + string.Join(", ", outside));

        // (3) Over HTTP: nothing else changes for a token holding every scope.
        var checkedRequests = 0;
        var mismatches = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var url = "/" + AccessSweepSupport.Concrete(endpoint.RoutePattern);
            foreach (var method in MethodsOf(endpoint))
            {
                if (TokenWriteAllowlist.Contains(Key(method, endpoint)) || (UnderExport(endpoint) && IsSafe(method)))
                    continue;
                var expected = await anonymous.SendAsync(AccessSweepSupport.Request(method, url));
                var actual = await bearer.SendAsync(AccessSweepSupport.Request(method, url));
                checkedRequests++;
                if (expected.StatusCode != actual.StatusCode)
                    mismatches.Add($"{method} {url}: {(int)expected.StatusCode} without credentials, {(int)actual.StatusCode} with a token");
            }
        }

        Assert.True(checkedRequests > 100, $"only {checkedRequests} requests were swept");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    /// <summary>
    /// 1.43.0 tracker sync (lane E): every tracker route - the user's own <c>/api/v1/trackers</c> and the admin's
    /// <c>/api/v1/admin/trackers</c> - is cookie + CSRF only. The expected set is listed here so a new tracker route is added on purpose;
    /// none names an export policy, the token-write marker or an antiforgery opt-out; a valid token holding EVERY scope gets 401 on each
    /// (with a real tracker slug, so an accepting route would answer otherwise).
    /// </summary>
    private static readonly string[] TrackerRoutes =
    [
        "GET api/v1/admin/trackers",
        "GET api/v1/admin/trackers/accounts",
        "GET api/v1/admin/trackers/nodes/{nodeId}/ids",
        "GET api/v1/admin/trackers/{slug}",
        "GET api/v1/trackers",
        "GET api/v1/trackers/attention",
        // 1.45.1 (B4): the rings of the cards on screen after Back (cookie only, like every tracker route).
        "GET api/v1/trackers/card-marks",
        "GET api/v1/trackers/nodes/{nodeId}/summary",
        "GET api/v1/trackers/sync-overrides",
        "GET api/v1/trackers/{slug}/entries",
        "GET api/v1/trackers/{slug}/series",
        "GET api/v1/trackers/{slug}/series/ids",
        "POST api/v1/admin/trackers/{slug}/accounts/{userId}/disconnect",
        "POST api/v1/trackers/{slug}/connect",
        "POST api/v1/trackers/{slug}/connect/password",
        "POST api/v1/trackers/{slug}/connect/start",
        "POST api/v1/trackers/{slug}/disconnect",
        "POST api/v1/trackers/{slug}/entries/batch",
        "POST api/v1/trackers/{slug}/entries/{seriesId}/from-tracker",
        "POST api/v1/trackers/{slug}/entries/{seriesId}/send",
        "POST api/v1/trackers/{slug}/entries/{seriesId}/set",
        "POST api/v1/trackers/{slug}/entries/{seriesId}/sync",
        // 1.44.0: "Set <tracker>'s volumes to N" (the user's click; cookie + CSRF like every tracker route).
        "POST api/v1/trackers/{slug}/entries/{seriesId}/volumes-from-chapters",
        "POST api/v1/trackers/{slug}/preview",
        "PUT api/v1/admin/trackers/nodes/{nodeId}/ids/{slug}",
        "PUT api/v1/admin/trackers/{slug}",
        "PUT api/v1/trackers/sync-overrides",
        "PUT api/v1/trackers/{slug}/entries/{seriesId}/dates",
        "PUT api/v1/trackers/{slug}/entries/{seriesId}/dont-sync",
        "PUT api/v1/trackers/{slug}/settings",
    ];

    [Fact]
    public async Task Sweep_TrackerRoutes_AreCookieOnly_AndRefuseEveryToken()
    {
        await using var factory = new ApiTokenTestFactory();
        var created = await factory.CreateTokenAsync(scopes: ExportApi.KnownScopes);
        var bearer = factory.BearerClient(created.Secret);

        var endpoints = AccessSweepSupport.Endpoints(factory.Services)
            .Where(e => (e.RoutePattern.RawText?.TrimStart('/') ?? string.Empty) is var raw
                && (raw.StartsWith("api/v1/trackers", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("api/v1/admin/trackers", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        static IEnumerable<string> MethodsOf(RouteEndpoint e) => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
        var keys = endpoints.SelectMany(e => MethodsOf(e).Select(m => m + " " + e.RoutePattern.RawText?.TrimStart('/'))).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(TrackerRoutes.Order(StringComparer.Ordinal), keys);

        foreach (var endpoint in endpoints)
        {
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
            Assert.DoesNotContain(ExportApi.Policy, policies);
            Assert.DoesNotContain(ExportApi.ScanPolicy, policies);
            Assert.All(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), a => Assert.True(string.IsNullOrEmpty(a.AuthenticationSchemes)));
            Assert.Null(endpoint.Metadata.GetMetadata<TokenWriteAllowedAttribute>());
            Assert.Null(endpoint.Metadata.GetMetadata<IgnoreAntiforgeryTokenAttribute>());
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            var url = "/" + AccessSweepSupport.Concrete(endpoint.RoutePattern, name => name == "slug" ? "mal" : "1");
            foreach (var method in MethodsOf(endpoint))
            {
                var response = await bearer.SendAsync(AccessSweepSupport.Request(method, url));
                Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {url}: {(int)response.StatusCode} with a token");
            }
        }
    }

    [Fact]
    public async Task AdminApi_GrantsOnlyTheScopesTickedAtCreation_AndRefusesUnknownOrEmptyScopes()
    {
        await using var factory = new ApiTokenTestFactory();
        var admin = await factory.AdminAsync();

        async Task<IReadOnlyList<string>> CreatedScopesAsync(object body)
        {
            var response = await admin.PostAsJsonAsync("/api/v1/admin/tokens", body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<CreateApiTokenResponse>(TestJson.Web))!.Token.Scopes;
        }

        // Left out or null: the read scope only (a 1.33.0 client keeps its behaviour).
        Assert.Equal(["metadata:read"], await CreatedScopesAsync(new { name = "absent", expiresInDays = 30 }));
        Assert.Equal(["metadata:read"], await CreatedScopesAsync(new { name = "null", expiresInDays = 30, scopes = (string[]?)null }));
        Assert.Equal(["library:scan"], await CreatedScopesAsync(new { name = "scan", expiresInDays = 30, scopes = new[] { "library:scan" } }));
        Assert.Equal(["metadata:read", "library:scan"],
            await CreatedScopesAsync(new { name = "both", expiresInDays = 30, scopes = new[] { "library:scan", "metadata:read", "library:scan" } }));

        foreach (var bad in new object[]
                 {
                     new { name = "empty", expiresInDays = 30, scopes = Array.Empty<string>() },
                     new { name = "unknown", expiresInDays = 30, scopes = new[] { "admin" } },
                     new { name = "mixed", expiresInDays = 30, scopes = new[] { "metadata:read", "library:write" } },
                     new { name = "case", expiresInDays = 30, scopes = new[] { "Library:Scan" } },
                     new { name = "space", expiresInDays = 30, scopes = new[] { " library:scan" } },
                     new { name = "nullentry", expiresInDays = 30, scopes = new string?[] { null } },
                 })
        {
            var response = await admin.PostAsJsonAsync("/api/v1/admin/tokens", bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_scope", (await response.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        }
        Assert.Equal(4, (await admin.GetFromJsonAsync<List<ApiTokenDto>>("/api/v1/admin/tokens", TestJson.Web) ?? []).Count);
    }

    [Fact]
    public async Task PerTokenCeiling_Answers429WithRetryAfter_PerToken_AndNeverLimitsTheAdminsCookie()
    {
        await using var factory = new ApiTokenTestFactory(new Dictionary<string, string?>
        {
            ["MangaPixer:Security:ApiTokens:RequestsPerMinute"] = "3",
        });
        var admin = await factory.AdminAsync();
        var first = await factory.CreateTokenAsync("first");
        var second = await factory.CreateTokenAsync("second");
        var client = factory.BearerClient(first.Secret);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Ping)).StatusCode);
        var limited = await client.GetAsync(Ping);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var retry));
        Assert.InRange(int.Parse(retry!.Single(), System.Globalization.CultureInfo.InvariantCulture), 1, 60);
        Assert.Equal("rate_limited", (await limited.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);

        // Another token has its own allowance; the admin's cookie is not limited.
        Assert.Equal(HttpStatusCode.OK, (await factory.BearerClient(second.Secret).GetAsync(Ping)).StatusCode);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Ping)).StatusCode);
    }

    [Fact]
    public async Task FailedAttempts_FromOneAddress_Answer429BeforeAnyTokenIsChecked_OtherAddressesAndCookiesUnaffected()
    {
        await using var factory = new ApiTokenTestFactory(new Dictionary<string, string?>
        {
            ["MangaPixer:Security:ApiTokens:FailedAttemptsPerIp"] = "3",
        }, rateLimitDisabled: false);
        var admin = await factory.AdminAsync();
        var created = await factory.CreateTokenAsync();
        var (wrong, _, _) = com.lifepixer.mangapixer.Server.Features.Tokens.ApiTokenSecret.Generate();

        HttpRequestMessage From(string address, string secret)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Ping);
            request.Headers.TryAddWithoutValidation("X-Test-Remote-Ip", address);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
            return request;
        }

        var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(From("203.0.113.7", wrong))).StatusCode);

        // Now even the VALID token from that address is refused: the limiter runs before the lookup.
        var blocked = await client.SendAsync(From("203.0.113.7", created.Secret));
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.TryGetValues("Retry-After", out var retry));
        Assert.InRange(int.Parse(retry!.Single(), System.Globalization.CultureInfo.InvariantCulture), 1, 300);
        Assert.Equal("too_many_attempts", (await blocked.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(From("203.0.113.8", created.Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Ping)).StatusCode);
    }

    [Fact]
    public async Task TheSecret_NeverAppearsInLogs_NorInTheList_NorInTheAuditTrail()
    {
        await using var factory = new ApiTokenTestFactory(rateLimitDisabled: false);
        var admin = await factory.AdminAsync();
        var created = await factory.CreateTokenAsync();
        var client = factory.BearerClient(created.Secret);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Ping)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/tokens")).StatusCode);
        Assert.False((await client.PostAsync(Ping, null)).IsSuccessStatusCode);
        (await admin.PostAsync($"/api/v1/admin/tokens/{created.Token.Id}/revoke", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Ping)).StatusCode);

        var list = await admin.GetStringAsync("/api/v1/admin/tokens");
        var audit = await admin.GetStringAsync("/api/v1/admin/audit?page=1&pageSize=50");
        var logs = factory.CapturedLogText();

        var secretBody = created.Secret["mpx_".Length..];
        foreach (var (what, text) in new[] { ("list", list), ("audit", audit), ("logs", logs) })
        {
            Assert.False(text.Contains(secretBody, StringComparison.Ordinal), $"the secret appears in the {what}");
            Assert.False(text.Contains(com.lifepixer.mangapixer.Server.Features.Tokens.ApiTokenSecret.Hash(created.Secret), StringComparison.Ordinal),
                $"the secret's hash appears in the {what}");
        }
        Assert.Contains(created.Token.Id, logs, StringComparison.Ordinal);
        Assert.Contains("api_token.create", audit, StringComparison.Ordinal);
        Assert.Contains("api_token.revoke", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminApi_ValidatesInput_AndCookiePostsStillNeedCsrf()
    {
        await using var factory = new ApiTokenTestFactory();
        var admin = await factory.AdminAsync();

        foreach (var bad in new object[]
                 {
                     new { name = "", expiresInDays = 30 },
                     new { name = new string('x', 65), expiresInDays = 30 },
                     new { name = "ok", expiresInDays = 7 },
                     new { name = "ok", expiresInDays = 0 },
                     new { name = "ok" }, // expiresInDays is required (null = never)
                 })
        {
            var response = await admin.PostAsJsonAsync("/api/v1/admin/tokens", bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        var never = await admin.PostAsJsonAsync("/api/v1/admin/tokens", new { name = "never", expiresInDays = (int?)null });
        Assert.Equal(HttpStatusCode.OK, never.StatusCode);
        Assert.Null((await never.Content.ReadFromJsonAsync<CreateApiTokenResponse>(TestJson.Web))!.Token.ExpiresAt);

        // The same admin session without the CSRF header: refused by antiforgery, as for every unsafe admin call.
        var noCsrf = factory.CreateClient();
        await noCsrf.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "admin", Password = ApiTokenTestFactory.AdminPassword });
        var refused = await noCsrf.PostAsJsonAsync("/api/v1/admin/tokens", new CreateApiTokenRequest { Name = "x", ExpiresInDays = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Single(await admin.GetFromJsonAsync<List<ApiTokenDto>>("/api/v1/admin/tokens", TestJson.Web) ?? []);
    }

    [Fact]
    public async Task ClearRevoked_RemovesOnlyRevokedAndExpiredTokens_AuditsEach_AndNeedsAnAdminCookieWithCsrf()
    {
        await using var factory = new ApiTokenTestFactory();
        var admin = await factory.AdminAsync();
        var active = await factory.CreateTokenAsync("active");
        var revoked = await factory.CreateTokenAsync("revoked");
        var expired = await factory.CreateTokenAsync("expired", 30);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/admin/tokens/{revoked.Token.Id}/revoke", null)).StatusCode);
        await factory.CreateUserAsync("second", "Second-Admin-Pass-1!", isAdmin: true);
        var second = await factory.LoginAsync("second", "Second-Admin-Pass-1!");
        var paused = await factory.CreateTokenAsync("paused", client: second);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var past = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.ApiTokens.Where(t => t.PublicId == expired.Token.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, past));
            // Its admin is demoted: the token is paused (it works again if they are an admin again), so it is kept.
            await db.Users.Where(u => u.NormalizedUserName == "SECOND").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsAdmin, false));
        }

        // A token cannot clear anything (cookie only), and a cookie POST without the CSRF header is refused.
        var anonymous = await factory.CreateClient().PostAsync("/api/v1/admin/tokens/clear-revoked", null);
        var withToken = await factory.BearerClient(active.Secret).PostAsync("/api/v1/admin/tokens/clear-revoked", null);
        Assert.Equal(anonymous.StatusCode, withToken.StatusCode);
        Assert.False(withToken.IsSuccessStatusCode);
        var noCsrf = factory.CreateClient();
        await noCsrf.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "admin", Password = ApiTokenTestFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.BadRequest, (await noCsrf.PostAsync("/api/v1/admin/tokens/clear-revoked", null)).StatusCode);
        Assert.Equal(4, (await admin.GetFromJsonAsync<List<ApiTokenDto>>("/api/v1/admin/tokens", TestJson.Web))!.Count);

        var response = await admin.PostAsync("/api/v1/admin/tokens/clear-revoked", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<ClearApiTokensResponse>(TestJson.Web))!.Removed);

        var left = (await admin.GetFromJsonAsync<List<ApiTokenDto>>("/api/v1/admin/tokens", TestJson.Web))!;
        Assert.Equal(new[] { paused.Token.Id, active.Token.Id }.Order(), left.Select(t => t.Id).Order());
        Assert.Equal(HttpStatusCode.OK, (await factory.BearerClient(active.Secret).GetAsync(Ping)).StatusCode); // untouched
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var deleted = await db.AuditEvents.AsNoTracking().Where(a => a.Action == "api_token.delete").Select(a => a.CorrelationId).ToListAsync();
            Assert.Equal(new[] { revoked.Token.Id, expired.Token.Id }.Order(), deleted.Order());
        }
        Assert.DoesNotContain(revoked.Secret, factory.CapturedLogText(), StringComparison.Ordinal);

        // Nothing left to clear: still 200, zero removed.
        var again = await admin.PostAsync("/api/v1/admin/tokens/clear-revoked", null);
        Assert.Equal(0, (await again.Content.ReadFromJsonAsync<ClearApiTokensResponse>(TestJson.Web))!.Removed);
    }
}
