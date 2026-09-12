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
/// <remarks>
/// Storage isolation seam (1.9.0 Lane C — restores <c>Server.Tests</c>
/// parallelism): storage roots are injected per-instance via
/// <see cref="TestHostStorageOverride"/>, a test-only ambient
/// (<see cref="AsyncLocal{T}"/>-based) override consulted at the top of
/// <c>Program.Main</c> — NOT via a process-global environment variable.
/// <c>IWebHostBuilder.ConfigureAppConfiguration</c> was tried first but does
/// not reach <c>Program.Main</c>'s synchronous pre-<c>Build()</c> reads for
/// this minimal-hosting entry point (see the remarks on
/// <see cref="TestHostStorageOverride"/> for why). Each factory instance
/// pushes its own unique DataRoot/CacheRoot/ScratchRoot immediately before
/// triggering its host's boot, on the same call stack, so concurrent
/// factories booting on different threads can never resolve the same
/// SQLite file. This eliminates the boot race that used to require both a
/// process-wide boot gate (<c>TestHostBootGate</c>, no longer used here) and
/// disabling assembly-level test parallelization (see TestParallelization.cs).
/// </remarks>
public sealed class MangaPlexWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "mangaplex-http-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly IReadOnlyDictionary<string, string?>? _extraConfiguration;

    public string DataRoot => Path.Combine(_tempRoot, "data");
    public string CacheRoot => Path.Combine(_tempRoot, "cache");
    public string ScratchRoot => Path.Combine(_tempRoot, "scratch");

    /// <summary>
    /// xUnit's <c>IClassFixture&lt;T&gt;</c> requires the fixture type to
    /// define exactly one PUBLIC constructor, so the extra-configuration
    /// overload below is private — reached instead through the static
    /// <see cref="WithExtraConfiguration"/> factory method — to keep this
    /// class usable both as a directly-constructed factory (most HTTP test
    /// classes) and as an <c>IClassFixture</c> (e.g. SearchHttpTests,
    /// CatalogHttpTests) without breaking either usage.
    /// </summary>
    public MangaPlexWebApplicationFactory() : this(null)
    {
    }

    /// <summary>
    /// Creates a factory with additional per-instance configuration overrides
    /// (e.g. a feature knob under test) layered on top of the isolated
    /// storage roots. Uses the same non-global injection seam as the storage
    /// roots — no environment variables are touched — so tests that need a
    /// specific config value no longer need
    /// <c>Environment.SetEnvironmentVariable</c> and the cross-test
    /// contamination that comes with process-global state.
    /// </summary>
    public static MangaPlexWebApplicationFactory WithExtraConfiguration(
        IReadOnlyDictionary<string, string?> extraConfiguration) => new(extraConfiguration);

    private MangaPlexWebApplicationFactory(IReadOnlyDictionary<string, string?>? extraConfiguration)
    {
        _extraConfiguration = extraConfiguration;

        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(ScratchRoot);

        // Push this factory's storage override for the duration of the
        // synchronous host boot triggered by CreateClient() below — see the
        // class-level remarks and TestHostStorageOverride for why this (and
        // not ConfigureAppConfiguration or an env var) is the seam that
        // actually reaches Program.Main in time, without any global state.
        using (TestHostStorageOverride.Push(BuildStorageOverride()))
        {
            // Force the host to boot now while the override is in effect.
            // The throwaway client is disposed at once; the host stays alive
            // until this factory is disposed. Subsequent CreateClient() calls
            // reuse the already-booted host (no further override read).
            using var bootClient = CreateClient();
        }
    }

    private StorageRootOverride BuildStorageOverride() => new(
        DataRoot: DataRoot,
        CacheRoot: CacheRoot,
        ScratchRoot: ScratchRoot,
        WorkerExecutablePath: "",
        RateLimitDisabled: true,
        ExtraConfiguration: _extraConfiguration);

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

            // Remove the thumbnail backfill hosted service — it depends on a
            // running worker pool and would log warnings in the test environment.
            var thumbBackfillDescriptor = services.FirstOrDefault(
                d => d.ImplementationType == typeof(ThumbnailBackfillHostedService));
            if (thumbBackfillDescriptor is not null)
                services.Remove(thumbBackfillDescriptor);
        });
    }

    /// <summary>
    /// Ensures the admin account exists via first-run setup (no default
    /// credential ships — audit finding F2), then returns an HttpClient with the
    /// auth cookie and CSRF header set. On a fresh DB the setup call both creates
    /// the admin and signs in; if a user already exists it falls back to login.
    /// The CSRF token is fetched AFTER sign-in because antiforgery tokens are
    /// tied to the user identity.
    /// </summary>
    public async Task<HttpClient> LoginAsAdminAsync(string password = "MangaPlex-Change-Me-Now!")
    {
        var client = CreateClient();

        // Setup and login are both [IgnoreAntiforgeryToken] so no CSRF header needed.
        var setupResponse = await client.PostAsJsonAsync("/api/v1/auth/setup", new SetupRequest
        {
            Username = "admin",
            Password = password,
        });

        if (setupResponse.StatusCode == HttpStatusCode.Conflict)
        {
            // Admin already created by an earlier call — sign in normally.
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

        // Get CSRF token AFTER sign-in — the token is tied to the authenticated identity.
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
    ///
    /// The authenticated client is cached per factory instance so that
    /// multiple test methods in the same IClassFixture class can reuse it
    /// without re-attempting the password change (which would fail on the
    /// second call because the password was already changed on the first).
    /// </summary>
    public async Task<HttpClient> LoginAsAdminWithChangedPasswordAsync(
        string currentPassword = "MangaPlex-Change-Me-Now!",
        string newPassword = "TestPassword123!")
    {
        // Return cached client if already authenticated
        if (_cachedAdminClient is not null)
            return _cachedAdminClient;

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

        _cachedAdminClient = freshClient;
        return freshClient;
    }

    private HttpClient? _cachedAdminClient;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
        base.Dispose(disposing);
    }
}
