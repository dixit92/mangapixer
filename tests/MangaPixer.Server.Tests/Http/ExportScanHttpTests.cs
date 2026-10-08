namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Features.Tokens;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests of the token-requested library scan (1.36.0, <c>POST /api/v1/export/libraries/{id}/scan</c>) through the real pipeline:
/// 202 starts the same full scan as Administration (lease owner <c>token:&lt;id&gt;</c>, one audit row); 409 + <c>Retry-After: 60</c>
/// while a scan runs (not counted toward the cooldown); 429 + <c>Retry-After</c> inside the per-library cooldown on the injected clock;
/// 403 without the scope and for a scan-only token on the export reads; 401 for a browser cookie with or without the CSRF header;
/// 404 for an unknown library; the token value never in logs or the audit trail. Synthetic libraries on empty temp folders.
/// </summary>
public sealed class ExportScanHttpTests : IDisposable
{
    private static readonly string[] ScanOnly = [ExportApi.ScanScope];
    private static readonly string[] ReadOnly = [ExportApi.Scope];

    private readonly List<string> _roots = [];

    private static string ScanUrl(string libraryId) => $"/api/v1/export/libraries/{libraryId}/scan";

    /// <summary>A library on a fresh empty folder (the scan finds nothing and completes quickly).</summary>
    private async Task<(long Id, string PublicId)> AddLibraryAsync(ApiTokenTestFactory factory, string publicId)
    {
        var root = Path.Combine(Path.GetTempPath(), "mangapixer-tokscan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _roots.Add(root);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var library = new LibraryEntity { PublicId = publicId, DisplayName = "Scan " + publicId, RootPath = root, CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return (library.Id, publicId);
    }

    private static async Task<ScanRunEntity?> RunAsync(ApiTokenTestFactory factory, string scanRunId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var id = OpaqueId.Decode(scanRunId);
        return await db.ScanRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
    }

    private static async Task<int> RunCountAsync(ApiTokenTestFactory factory, long libraryId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().ScanRuns.CountAsync(r => r.LibraryId == libraryId);
    }

    /// <summary>Waits until the background scan of <paramref name="scanRunId"/> has left the running state.</summary>
    private static async Task WaitFinishedAsync(ApiTokenTestFactory factory, string scanRunId)
    {
        for (var i = 0; i < 300; i++)
        {
            if ((await RunAsync(factory, scanRunId))?.Status is not 1)
                return;
            await Task.Delay(50);
        }
        Assert.Fail("the scan did not finish in time");
    }

    private static async Task<string> StartedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode}: {body}");
        var dto = JsonSerializer.Deserialize<ScanTriggeredDto>(body, TestJson.Web)!;
        Assert.False(string.IsNullOrEmpty(dto.ScanRunId));
        return dto.ScanRunId;
    }

    private static int RetryAfter(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values), "no Retry-After header");
        return int.Parse(values!.Single(), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))?.Error;

    [Fact]
    public async Task AScanToken_StartsAFullScan_202_TheLeaseOwnerIsTheToken_AndOneAuditRowNamesTheOwnerLibraryAndToken()
    {
        await using var factory = new ApiTokenTestFactory();
        var admin = await factory.AdminAsync();
        var (libraryId, publicId) = await AddLibraryAsync(factory, "scanlib1");
        var created = await factory.CreateTokenAsync("MangaList scan", scopes: ScanOnly);
        Assert.Equal(ScanOnly, created.Token.Scopes);

        var runId = await StartedAsync(await factory.BearerClient(created.Secret).PostAsync(ScanUrl(publicId), null));

        var run = await RunAsync(factory, runId);
        Assert.NotNull(run);
        Assert.Equal(libraryId, run!.LibraryId);
        Assert.Equal("token:" + created.Token.Id, run.LeaseOwner);
        await WaitFinishedAsync(factory, runId);
        Assert.Equal(2, (await RunAsync(factory, runId))!.Status); // completed

        // The admin's scan history lists the run.
        var history = await admin.GetFromJsonAsync<List<ScanRunDto>>($"/api/v1/admin/libraries/{publicId}/scans", TestJson.Web);
        Assert.Contains(history!, h => h.Id == runId);

        // Exactly one audit row: actor = the token's owner, target = the library, correlation = the token's public id.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var row = Assert.Single(await db.AuditEvents.AsNoTracking().Where(a => a.Action == AuditActions.LibraryScanRequest).ToListAsync());
            var ownerId = await db.Users.Where(u => u.UserName == "admin").Select(u => u.Id).SingleAsync();
            Assert.Equal(AuditResults.Success, row.Result);
            Assert.Equal(ownerId, row.ActorUserId);
            Assert.Equal(libraryId, row.TargetLibraryId);
            Assert.Equal(created.Token.Id, row.CorrelationId);
        }
        var audit = await admin.GetStringAsync("/api/v1/admin/audit?page=1&pageSize=50");
        Assert.Contains("library.scan.request", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhileAScanOfThatLibraryRuns_409WithRetryAfter60_AndThe409DoesNotStartTheCooldown()
    {
        await using var factory = new ApiTokenTestFactory();
        var (libraryId, publicId) = await AddLibraryAsync(factory, "scanlib2");
        var client = factory.BearerClient((await factory.CreateTokenAsync(scopes: ScanOnly)).Secret);

        // An admin's scan holds the lease (deterministic: no race with a real scan finishing first).
        long leaseId;
        using (var scope = factory.Services.CreateScope())
        {
            var leases = scope.ServiceProvider.GetRequiredService<ScanLeaseService>();
            leaseId = (await leases.AcquireLeaseAsync(libraryId, "server:1", TimeSpan.FromMinutes(30)))!.Id;
        }

        var busy = await client.PostAsync(ScanUrl(publicId), null);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal(60, RetryAfter(busy));
        Assert.Equal("scan_in_progress", await ErrorAsync(busy));
        Assert.Equal(1, await RunCountAsync(factory, libraryId));

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ScanLeaseService>().ReleaseLeaseAsync(leaseId, success: true);

        // The 409 did not count: the retry starts the scan at once.
        await StartedAsync(await client.PostAsync(ScanUrl(publicId), null));
    }

    [Fact]
    public async Task InsideTheCooldown_429WithRetryAfter_PerLibrary_AndAfterItTheScanStarts_OnTheInjectedClock()
    {
        var clock = new ManualTime(DateTimeOffset.UtcNow);
        await using var factory = new ApiTokenTestFactory(clock: clock);
        var (_, first) = await AddLibraryAsync(factory, "scanlib3");
        var (_, second) = await AddLibraryAsync(factory, "scanlib4");
        var created = await factory.CreateTokenAsync(scopes: ScanOnly);
        var client = factory.BearerClient(created.Secret);

        var runId = await StartedAsync(await client.PostAsync(ScanUrl(first), null));
        await WaitFinishedAsync(factory, runId);

        clock.Advance(TimeSpan.FromMinutes(2));
        var cooling = await client.PostAsync(ScanUrl(first), null);
        Assert.Equal(HttpStatusCode.TooManyRequests, cooling.StatusCode);
        Assert.Equal(180, RetryAfter(cooling)); // 5 min default - 2 min
        Assert.Equal("scan_cooldown", await ErrorAsync(cooling));

        // Per library: another library is not cooling down. Another token does not reset the window either.
        await StartedAsync(await client.PostAsync(ScanUrl(second), null));
        var other = factory.BearerClient((await factory.CreateTokenAsync("other", scopes: ScanOnly)).Secret);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await other.PostAsync(ScanUrl(first), null)).StatusCode);

        clock.Advance(TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(1));
        Assert.Equal(1, RetryAfter(await client.PostAsync(ScanUrl(first), null)));

        clock.Advance(TimeSpan.FromSeconds(1));
        await StartedAsync(await client.PostAsync(ScanUrl(first), null));
    }

    [Fact]
    public async Task TheCooldown_IsConfigurable_AndZeroTurnsItOff()
    {
        await using var factory = new ApiTokenTestFactory(new Dictionary<string, string?>
        {
            ["MangaPixer:Security:ApiTokens:ScanCooldownMinutes"] = "0",
        });
        var (_, publicId) = await AddLibraryAsync(factory, "scanlib5");
        var client = factory.BearerClient((await factory.CreateTokenAsync(scopes: ScanOnly)).Secret);

        var runId = await StartedAsync(await client.PostAsync(ScanUrl(publicId), null));
        await WaitFinishedAsync(factory, runId);
        await StartedAsync(await client.PostAsync(ScanUrl(publicId), null));
    }

    [Fact]
    public async Task Scopes_AreSeparate_AReadTokenCannotScan_AndAScanOnlyTokenCannotReadTheExport()
    {
        await using var factory = new ApiTokenTestFactory();
        var (libraryId, publicId) = await AddLibraryAsync(factory, "scanlib6");
        var read = factory.BearerClient((await factory.CreateTokenAsync("read", scopes: ReadOnly)).Secret);
        var scan = factory.BearerClient((await factory.CreateTokenAsync("scan", scopes: ScanOnly)).Secret);
        var both = factory.BearerClient((await factory.CreateTokenAsync("both", scopes: [ExportApi.Scope, ExportApi.ScanScope])).Secret);
        var legacy = factory.BearerClient((await factory.CreateTokenAsync("legacy")).Secret); // scopes left out = read only

        foreach (var client in new[] { read, legacy })
        {
            var refused = await client.PostAsync(ScanUrl(publicId), null);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        Assert.Equal(0, await RunCountAsync(factory, libraryId));

        foreach (var url in new[] { "/api/v1/export/ping", "/api/v1/export/libraries", $"/api/v1/export/metadata?library={publicId}" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await scan.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await read.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await both.GetAsync(url)).StatusCode);
        }
        await StartedAsync(await both.PostAsync(ScanUrl(publicId), null));
    }

    [Fact]
    public async Task ABrowserCookie_NeverReachesTheScanRoute_401_WithAndWithoutTheCsrfHeader_EvenForAnAdmin()
    {
        await using var factory = new ApiTokenTestFactory();
        var (libraryId, publicId) = await AddLibraryAsync(factory, "scanlib7");
        var withCsrf = await factory.AdminAsync();
        var noCsrf = factory.CreateClient();
        (await noCsrf.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "admin", Password = ApiTokenTestFactory.AdminPassword }))
            .EnsureSuccessStatusCode();

        foreach (var client in new[] { withCsrf, noCsrf, factory.CreateClient() })
        {
            var response = await client.PostAsync(ScanUrl(publicId), null);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        }
        Assert.Equal(0, await RunCountAsync(factory, libraryId));
        // The same admin cookie still scans through Administration (CSRF-checked), so the 401 is the route, not the session.
        Assert.Equal(HttpStatusCode.Accepted, (await withCsrf.PostAsync($"/api/v1/admin/libraries/{publicId}/scan", null)).StatusCode);
    }

    [Fact]
    public async Task AnUnknownLibrary_Is404_AndARevokedToken_Is401()
    {
        await using var factory = new ApiTokenTestFactory();
        var admin = await factory.AdminAsync();
        var created = await factory.CreateTokenAsync(scopes: ScanOnly);
        var client = factory.BearerClient(created.Secret);

        var missing = await client.PostAsync(ScanUrl("nosuchlib"), null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("library_not_found", await ErrorAsync(missing));

        (await admin.PostAsync($"/api/v1/admin/tokens/{created.Token.Id}/revoke", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(ScanUrl("nosuchlib"), null)).StatusCode);
    }

    [Fact]
    public async Task TheScheme_AcceptsAPostOnlyOnTheMarkedEndpoint_AndThePrincipalCarriesTheOwnerButNoNameOrRole()
    {
        await using var factory = new ApiTokenTestFactory();
        var created = await factory.CreateTokenAsync(scopes: ScanOnly);
        var marked = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new TokenWriteAllowedAttribute()), "marked");
        var plain = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "plain");

        async Task<AuthenticateResult> AuthenticateAsync(string method, Endpoint? endpoint)
        {
            // A request scope of its own: the scoped handler provider caches a scheme's handler (and its result) per request.
            using var scope = factory.Services.CreateScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Method = method;
            context.Request.Path = ScanUrl("x");
            context.Request.Headers.Authorization = "Bearer " + created.Secret;
            if (endpoint is not null)
                context.SetEndpoint(endpoint);
            return await context.AuthenticateAsync(ExportApi.TokenScheme);
        }

        var post = await AuthenticateAsync("POST", marked);
        Assert.True(post.Succeeded, post.Failure?.Message ?? "no result");
        var principal = post.Principal!;
        Assert.Equal(created.Token.Id, principal.FindFirst(ApiTokenClaims.TokenId)?.Value);
        Assert.Equal(new[] { ExportApi.ScanScope }, principal.FindAll(ApiTokenClaims.Scope).Select(c => c.Value));
        Assert.NotNull(principal.FindFirst(ApiTokenClaims.OwnerUserId));
        Assert.Null(principal.Identity!.Name);
        Assert.Null(principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier));
        Assert.False(principal.IsInRole("admin"));
        Assert.DoesNotContain(principal.Claims, c => c.Type == System.Security.Claims.ClaimTypes.Role);

        foreach (var (method, endpoint) in new (string, Endpoint?)[]
                 {
                     ("PUT", marked), ("DELETE", marked), ("PATCH", marked), ("POST", plain), ("POST", null),
                 })
        {
            var refused = await AuthenticateAsync(method, endpoint);
            Assert.False(refused.Succeeded, $"{method} on {endpoint?.DisplayName ?? "no endpoint"} was accepted");
            Assert.Equal("method_not_allowed", refused.Failure?.Message);
        }
    }

    [Fact]
    public async Task TheSecret_NeverAppearsInLogs_NorInTheAuditTrail_OfScanRequests()
    {
        var clock = new ManualTime(DateTimeOffset.UtcNow);
        await using var factory = new ApiTokenTestFactory(rateLimitDisabled: false, clock: clock);
        var admin = await factory.AdminAsync();
        var (libraryId, publicId) = await AddLibraryAsync(factory, "scanlib8");
        var created = await factory.CreateTokenAsync(scopes: ScanOnly);
        var read = await factory.CreateTokenAsync("read", scopes: ReadOnly);
        var client = factory.BearerClient(created.Secret);

        var runId = await StartedAsync(await client.PostAsync(ScanUrl(publicId), null));          // 202
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync(ScanUrl(publicId), null)).StatusCode); // 429
        await WaitFinishedAsync(factory, runId);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ScanLeaseService>().AcquireLeaseAsync(libraryId, "server:1", TimeSpan.FromMinutes(30));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(ScanUrl(publicId), null)).StatusCode);       // 409
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(ScanUrl("nosuchlib"), null)).StatusCode);     // 404
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.BearerClient(read.Secret).PostAsync(ScanUrl(publicId), null)).StatusCode); // 403

        var audit = await admin.GetStringAsync("/api/v1/admin/audit?page=1&pageSize=50");
        var logs = factory.CapturedLogText();
        foreach (var (what, text) in new[] { ("audit", audit), ("logs", logs) })
        {
            foreach (var secret in new[] { created.Secret, read.Secret })
            {
                Assert.False(text.Contains(secret["mpx_".Length..], StringComparison.Ordinal), $"a secret appears in the {what}");
                Assert.False(text.Contains(ApiTokenSecret.Hash(secret), StringComparison.Ordinal), $"a secret's hash appears in the {what}");
            }
        }
        Assert.Contains(created.Token.Id, logs, StringComparison.Ordinal);
        Assert.Contains(created.Token.Id, audit, StringComparison.Ordinal);
        Assert.Contains("started on request of API token", logs, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }
}
