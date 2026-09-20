namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Updates;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Tests.Server.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP integration tests (WebApplicationFactory) for the Update Checker
/// endpoints: <c>GET /api/v1/operations/update-check</c> and
/// <c>PUT /api/v1/operations/update-check/settings</c>. Verifies admin gating
/// (non-admin → 403) and the enable → check flow. The GitHub call is mocked by
/// replacing the named client's primary handler — no test touches api.github.com.
/// </summary>
[Collection("HttpSerial")]
public sealed class UpdateCheckHttpTests : IDisposable
{
    private readonly UpdateCheckWebApplicationFactory _factory;

    public UpdateCheckHttpTests() => _factory = new UpdateCheckWebApplicationFactory();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task GetUpdateCheck_NonAdmin_Returns403()
    {
        var reader = await _factory.CreateNonAdminClientAsync();
        var response = await reader.GetAsync("/api/v1/operations/update-check");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PutUpdateCheckSettings_NonAdmin_Returns403()
    {
        var reader = await _factory.CreateNonAdminClientAsync();
        var response = await reader.PutAsJsonAsync(
            "/api/v1/operations/update-check/settings",
            new UpdateCheckSettingsRequest { Enabled = true });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetUpdateCheck_Admin_DefaultsToDisabledAndDoesNotCallOut()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await admin.GetAsync("/api/v1/operations/update-check");

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<UpdateCheckStatusDto>();
        Assert.NotNull(dto);
        Assert.False(dto!.Enabled);
        Assert.Null(dto.LatestVersion);
        Assert.False(dto.UpdateAvailable);
        // Disabled → the mocked handler must not have been invoked.
        Assert.Equal(0, _factory.Handler.CallCount);
    }

    [Fact]
    public async Task PutUpdateCheckSettings_Enable_RunsCheckAndReportsUpdate()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var putResponse = await admin.PutAsJsonAsync(
            "/api/v1/operations/update-check/settings",
            new UpdateCheckSettingsRequest { Enabled = true });

        putResponse.EnsureSuccessStatusCode();
        var dto = await putResponse.Content.ReadFromJsonAsync<UpdateCheckStatusDto>();
        Assert.NotNull(dto);
        Assert.True(dto!.Enabled);
        Assert.Equal("99.0.0", dto.LatestVersion);   // from the mocked tag "v99.0.0"
        Assert.True(dto.UpdateAvailable);
        Assert.NotNull(dto.LastChecked);
        Assert.Equal(1, _factory.Handler.CallCount);
    }

    [Fact]
    public async Task GetUpdateCheck_Force_ChecksAgainAfterEnabling()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Enable (runs one check).
        await admin.PutAsJsonAsync(
            "/api/v1/operations/update-check/settings",
            new UpdateCheckSettingsRequest { Enabled = true });
        Assert.Equal(1, _factory.Handler.CallCount);

        // "Check now" forces another despite the cadence gate.
        var response = await admin.GetAsync("/api/v1/operations/update-check?force=true");
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<UpdateCheckStatusDto>();
        Assert.True(dto!.UpdateAvailable);
        Assert.Equal(2, _factory.Handler.CallCount);
    }
}

/// <summary>
/// Standalone factory (MangaPixerWebApplicationFactory is sealed) that boots the
/// app with isolated storage and replaces the Update Checker's named HttpClient
/// primary handler with a counting stub, so the GitHub call is fully mocked.
/// </summary>
public sealed class UpdateCheckWebApplicationFactory : WebApplicationFactory<com.lifepixer.mangapixer.Server.Program>
{
    private readonly string _tempRoot;
    private HttpClient? _cachedAdminClient;

    /// <summary>The stub that stands in for GitHub; exposes a call counter.</summary>
    public CountingHandler Handler { get; } = CountingHandler.ReturningTag("v99.0.0");

    public UpdateCheckWebApplicationFactory()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mangapixer-updchk-http-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "data"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "cache"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "scratch"));

        var storageOverride = new StorageRootOverride(
            DataRoot: Path.Combine(_tempRoot, "data"),
            CacheRoot: Path.Combine(_tempRoot, "cache"),
            ScratchRoot: Path.Combine(_tempRoot, "scratch"),
            WorkerExecutablePath: "",
            RateLimitDisabled: true);

        using (TestHostStorageOverride.Push(storageOverride))
        {
            using var bootClient = CreateClient();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            var workerHosted = services.FirstOrDefault(
                d => d.ImplementationType == typeof(MediaWorkerHostedService));
            if (workerHosted is not null)
                services.Remove(workerHosted);

            var thumbBackfill = services.FirstOrDefault(
                d => d.ImplementationType == typeof(ThumbnailBackfillHostedService));
            if (thumbBackfill is not null)
                services.Remove(thumbBackfill);

            // Replace the primary handler of the Update Checker's named client so
            // the outbound GitHub call is intercepted by the counting stub.
            services.AddHttpClient(UpdateCheckService.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Handler);
        });
    }

    public async Task<HttpClient> LoginAsAdminAsync(string password = "MangaPixer-Change-Me-Now!")
    {
        var client = CreateClient();

        var setupResponse = await client.PostAsJsonAsync("/api/v1/auth/setup", new SetupRequest
        {
            Username = "admin",
            Password = password,
        });

        if (setupResponse.StatusCode == HttpStatusCode.Conflict)
        {
            var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
            {
                Username = "admin",
                Password = password,
            });
            loginResponse.EnsureSuccessStatusCode();
        }
        else
        {
            setupResponse.EnsureSuccessStatusCode();
        }

        await AttachCsrfAsync(client);
        return client;
    }

    public async Task<HttpClient> LoginAsAdminWithChangedPasswordAsync(
        string currentPassword = "MangaPixer-Change-Me-Now!",
        string newPassword = "TestPassword123!")
    {
        if (_cachedAdminClient is not null)
            return _cachedAdminClient;

        var client = await LoginAsAdminAsync(currentPassword);

        var changeResponse = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = currentPassword,
            NewPassword = newPassword,
        });
        changeResponse.EnsureSuccessStatusCode();

        var fresh = CreateClient();
        var reLogin = await fresh.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "admin",
            Password = newPassword,
        });
        reLogin.EnsureSuccessStatusCode();
        await AttachCsrfAsync(fresh);

        _cachedAdminClient = fresh;
        return fresh;
    }

    /// <summary>
    /// Creates a non-admin user (via the admin) and returns a client
    /// authenticated as that user, with its forced password change cleared.
    /// </summary>
    public async Task<HttpClient> CreateNonAdminClientAsync()
    {
        var admin = await LoginAsAdminWithChangedPasswordAsync();
        var create = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "reader1",
            Password = "ReaderPass123!",
            IsAdmin = false,
        });
        create.EnsureSuccessStatusCode();

        var reader = CreateClient();
        var login = await reader.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "reader1",
            Password = "ReaderPass123!",
        });
        login.EnsureSuccessStatusCode();
        await AttachCsrfAsync(reader);

        var change = await reader.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "ReaderPass123!",
            NewPassword = "ReaderPass456!",
        });
        change.EnsureSuccessStatusCode();

        var fresh = CreateClient();
        var reLogin = await fresh.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "reader1",
            Password = "ReaderPass456!",
        });
        reLogin.EnsureSuccessStatusCode();
        await AttachCsrfAsync(fresh);
        return fresh;
    }

    private static async Task AttachCsrfAsync(HttpClient client)
    {
        var csrfResponse = await client.GetAsync("/api/v1/auth/csrf");
        csrfResponse.EnsureSuccessStatusCode();
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        Assert.NotNull(csrf);
        client.DefaultRequestHeaders.Remove("X-MangaPixer-Csrf");
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
        base.Dispose(disposing);
    }

    /// <summary>Counting stub handler standing in for the GitHub Releases API.</summary>
    public sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _factory;
        public int CallCount { get; private set; }

        private CountingHandler(Func<HttpResponseMessage> factory) => _factory = factory;

        public static CountingHandler ReturningTag(string tag) => new(() =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"tag_name\":\"{tag}\"}}", Encoding.UTF8, "application/json"),
            });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_factory());
        }
    }
}
