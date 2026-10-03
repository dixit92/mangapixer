namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Tests.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Xunit;

/// <summary>
/// A test host for the personal access token tests (1.33.0): its own storage, optional configuration, the login /
/// failed-attempt limiter switch, a Serilog collecting sink (every level) and the test-only peer-address seam
/// (<c>X-Test-Remote-Ip</c>) of the login rate-limit tests.
/// </summary>
public sealed class ApiTokenTestFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "TestPassword123!";

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "mangapixer-tok-" + Guid.NewGuid().ToString("N")[..8]);
    private HttpClient? _admin;

    public ApiTokenTestFactory(IReadOnlyDictionary<string, string?>? configuration = null, bool rateLimitDisabled = true)
    {
        var data = Path.Combine(_tempRoot, "data");
        var cache = Path.Combine(_tempRoot, "cache");
        var scratch = Path.Combine(_tempRoot, "scratch");
        foreach (var dir in new[] { data, cache, scratch })
            Directory.CreateDirectory(dir);

        var extra = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["MangaPixer:Scanning:Scheduler:Enabled"] = "false",
            ["Metadata:AutoMatch:WorkerEnabled"] = "false",
            ["Covers:SweepEnabled"] = "false",
        };
        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
            extra[key] = value;

        using (TestHostStorageOverride.Push(new StorageRootOverride(data, cache, scratch, WorkerExecutablePath: "",
                   RateLimitDisabled: rateLimitDisabled, ExtraConfiguration: extra)))
        {
            using var boot = CreateClient();
        }
    }

    public CollectingSink Sink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            foreach (var type in new[] { typeof(MediaWorkerHostedService), typeof(ThumbnailBackfillHostedService), typeof(ComicInfoBackfillHostedService) })
            {
                var descriptor = services.FirstOrDefault(d => d.ImplementationType == type);
                if (descriptor is not null)
                    services.Remove(descriptor);
            }

            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, RemoteIpStartupFilter>();
            TestHostLogging.Wrap(services, inner => new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(Sink)
                .WriteTo.Logger(inner)
                .CreateLogger());
        });
    }

    /// <summary>First-run setup + password change; the signed-in admin client with its CSRF header, cached.</summary>
    public async Task<HttpClient> AdminAsync()
    {
        if (_admin is not null)
            return _admin;
        var setup = CreateClient();
        (await setup.PostAsJsonAsync("/api/v1/auth/setup", new SetupRequest { Username = "admin", Password = "MangaPixer-Change-Me-Now!" })).EnsureSuccessStatusCode();
        await AddCsrfAsync(setup);
        (await setup.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "MangaPixer-Change-Me-Now!",
            NewPassword = AdminPassword,
        })).EnsureSuccessStatusCode();
        return _admin = await LoginAsync("admin", AdminPassword);
    }

    /// <summary>A signed-in client for <paramref name="username"/> with its CSRF header.</summary>
    public async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = password })).EnsureSuccessStatusCode();
        await AddCsrfAsync(client);
        return client;
    }

    /// <summary>Creates a user with a password (no forced change for a password-created user is assumed; it is cleared here).</summary>
    public async Task CreateUserAsync(string username, string password, bool isAdmin)
    {
        var admin = await AdminAsync();
        (await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = username, Password = password, IsAdmin = isAdmin }))
            .EnsureSuccessStatusCode();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<com.lifepixer.mangapixer.Server.Persistence.MangaPixerDbContext>();
        var user = db.Users.Single(u => u.NormalizedUserName == username.ToUpperInvariant());
        user.ForcePasswordChange = false;
        await db.SaveChangesAsync();
    }

    /// <summary>Creates a token through the admin API with <paramref name="client"/> (default: the first admin).</summary>
    public async Task<CreateApiTokenResponse> CreateTokenAsync(string name = "MangaList", int? expiresInDays = 365, HttpClient? client = null)
    {
        client ??= await AdminAsync();
        var response = await client.PostAsJsonAsync("/api/v1/admin/tokens", new CreateApiTokenRequest { Name = name, ExpiresInDays = expiresInDays });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateApiTokenResponse>(TestJson.Web))!;
    }

    /// <summary>A client with no cookie that sends <paramref name="secret"/> as a bearer token.</summary>
    public HttpClient BearerClient(string secret)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    /// <summary>Everything the host logged, rendered with all property values and exceptions.</summary>
    public string CapturedLogText() => string.Join("\n", Sink.Events.Select(e =>
        e.RenderMessage() + " " + string.Join(" ", e.Properties.Select(p => p.Key + "=" + p.Value)) + " " + e.Exception));

    private static async Task AddCsrfAsync(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<CsrfTokenDto>("/api/v1/auth/csrf");
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { Directory.Delete(_tempRoot, true); } catch { /* best effort */ }
        }
        base.Dispose(disposing);
    }

    private sealed class RemoteIpStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Remote-Ip", out var raw) && IPAddress.TryParse(raw.ToString(), out var address))
                    context.Connection.RemoteIpAddress = address;
                await nextMiddleware();
            });
            next(app);
        };
    }
}
