namespace com.lifepixer.mangaplex.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Server;
using com.lifepixer.mangaplex.Server.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// WebApplicationFactory for MangaPlex HTTP integration tests.
/// Uses a temp DataRoot/CacheRoot/ScratchRoot so each test run is isolated.
/// The media worker hosted service is replaced with a no-op so tests don't
/// spawn real worker processes.
/// </summary>
public sealed class MangaPlexWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "mangaplex-http-" + Guid.NewGuid().ToString("N")[..8]);

    public string DataRoot => Path.Combine(_tempRoot, "data");
    public string CacheRoot => Path.Combine(_tempRoot, "cache");
    public string ScratchRoot => Path.Combine(_tempRoot, "scratch");

    // Environment variables set before the host starts. These are read by
    // builder.Configuration.AddEnvironmentVariables() in Program.cs.
    // We save and restore them so parallel test classes don't interfere.
    private readonly Dictionary<string, string?> _savedEnv = new();

    public MangaPlexWebApplicationFactory()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(ScratchRoot);

        SetEnv("MangaPlex__Storage__DataRoot", DataRoot);
        SetEnv("MangaPlex__Storage__CacheRoot", CacheRoot);
        SetEnv("MangaPlex__Storage__ScratchRoot", ScratchRoot);
        SetEnv("Media__WorkerExecutablePath", "");
        SetEnv("MangaPlex__Security__RateLimit__Disabled", "true");
    }

    private void SetEnv(string key, string value)
    {
        _savedEnv[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Remove the worker pool hosted service so tests don't spawn workers.
            var workerHostedDescriptor = services.FirstOrDefault(
                d => d.ImplementationType == typeof(MediaWorkerHostedService));
            if (workerHostedDescriptor is not null)
                services.Remove(workerHostedDescriptor);
        });
    }

    /// <summary>
    /// Logs in as the default admin and returns an HttpClient with the auth
    /// cookie and CSRF header set. The CSRF token is fetched AFTER login
    /// because antiforgery tokens are tied to the user identity.
    /// </summary>
    public async Task<HttpClient> LoginAsAdminAsync(string password = "MangaPlex-Change-Me-Now!")
    {
        var client = CreateClient();

        // Login first — login is [IgnoreAntiforgeryToken] so no CSRF header needed.
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "admin",
            Password = password,
        });
        loginResponse.EnsureSuccessStatusCode();

        // Get CSRF token AFTER login — the token is tied to the authenticated identity.
        var csrfResponse = await client.GetAsync("/api/v1/auth/csrf");
        csrfResponse.EnsureSuccessStatusCode();
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        Assert.NotNull(csrf);
        client.DefaultRequestHeaders.Add("X-MangaPlex-Csrf", csrf!.Token);

        return client;
    }

    /// <summary>
    /// Logs in as admin, changes the password, re-logs in, and returns the
    /// authenticated client. Many endpoints require ForcePasswordChange to be
    /// cleared first.
    /// </summary>
    public async Task<HttpClient> LoginAsAdminWithChangedPasswordAsync(
        string currentPassword = "MangaPlex-Change-Me-Now!",
        string newPassword = "TestPassword123!")
    {
        var client = await LoginAsAdminAsync(currentPassword);

        // Change password
        var changeResponse = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = currentPassword,
            NewPassword = newPassword,
        });
        changeResponse.EnsureSuccessStatusCode();

        // Re-login with new password (sessions were revoked)
        var freshClient = CreateClient();
        var loginResponse = await freshClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "admin",
            Password = newPassword,
        });
        loginResponse.EnsureSuccessStatusCode();

        // Get CSRF token after re-login
        var csrfResponse = await freshClient.GetAsync("/api/v1/auth/csrf");
        csrfResponse.EnsureSuccessStatusCode();
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        Assert.NotNull(csrf);
        freshClient.DefaultRequestHeaders.Add("X-MangaPlex-Csrf", csrf!.Token);

        return freshClient;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Restore environment variables
            foreach (var kvp in _savedEnv)
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);

            try { Directory.Delete(_tempRoot, true); } catch { }
        }
        base.Dispose(disposing);
    }
}
