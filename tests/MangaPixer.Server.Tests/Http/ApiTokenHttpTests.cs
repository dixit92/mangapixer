namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
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

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var checkedRequests = 0;
        var mismatches = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var url = "/" + Concrete(endpoint.RoutePattern);
            if (url.StartsWith("/" + ExportApi.RoutePrefix + "/", StringComparison.OrdinalIgnoreCase))
                continue;
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var method in methods)
            {
                var expected = await anonymous.SendAsync(Request(method, url));
                var actual = await bearer.SendAsync(Request(method, url));
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

    /// <summary>The route pattern with every parameter filled with a placeholder.</summary>
    private static string Concrete(RoutePattern pattern)
    {
        var segments = pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart => "1",
            _ => string.Empty,
        })));
        return string.Join("/", segments);
    }

    private static HttpRequestMessage Request(string method, string url)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is not ("GET" or "HEAD" or "DELETE" or "OPTIONS"))
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return request;
    }
}
