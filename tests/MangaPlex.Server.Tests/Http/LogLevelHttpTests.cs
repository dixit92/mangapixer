namespace com.lifepixer.mangaplex.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Server.Hosting;
using com.lifepixer.mangaplex.Server.Operations;
using com.lifepixer.mangaplex.TestSupport.Hosting;
using com.lifepixer.mangaplex.Tests.Server.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

/// <summary>
/// HTTP integration tests for the admin log-level control feature (section 9).
/// Verifies that PUT /api/v1/operations/logging changes the live Serilog
/// minimum level, that non-admin users get 403, invalid levels get 400,
/// and the level-change event itself is logged at Information.
/// </summary>
[Collection("HttpSerial")]
public sealed class LogLevelHttpTests : IDisposable
{
    private readonly LogLevelWebApplicationFactory _factory;

    public LogLevelHttpTests()
    {
        _factory = new LogLevelWebApplicationFactory();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task GetLoggingLevel_ReturnsInformation()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.GetAsync("/api/v1/operations/logging");

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Information", dto.GetProperty("level").GetString());
    }

    [Fact]
    public async Task PutLoggingLevel_Debug_EnablesDebugEvents()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Clear any startup events
        _factory.Sink.Clear();

        // PUT Debug
        var putResponse = await client.PutAsJsonAsync("/api/v1/operations/logging",
            new { level = "Debug" });
        putResponse.EnsureSuccessStatusCode();
        var dto = await putResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Debug", dto.GetProperty("level").GetString());

        // Emit a Debug event via the Serilog pipeline
        Log.Debug("Test debug message for level switch verification");

        // The Debug event should be captured
        Assert.True(_factory.Sink.ContainsMessage("Test debug message for level switch verification"),
            "Debug event should be captured when level is Debug");
    }

    [Fact]
    public async Task PutLoggingLevel_Warning_SuppressesDebugEvents()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Set to Debug first
        await client.PutAsJsonAsync("/api/v1/operations/logging",
            new { level = "Debug" });

        _factory.Sink.Clear();

        // Now set to Warning
        var putResponse = await client.PutAsJsonAsync("/api/v1/operations/logging",
            new { level = "Warning" });
        putResponse.EnsureSuccessStatusCode();

        // Emit a Debug event
        Log.Debug("This should not appear after Warning switch");

        // The Debug event should NOT be captured
        Assert.False(_factory.Sink.ContainsMessage("This should not appear after Warning switch"),
            "Debug event should NOT be captured when level is Warning");
    }

    [Fact]
    public async Task PutLoggingLevel_NonAdmin_Returns403()
    {
        var adminClient = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Create a non-admin user
        await adminClient.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "reader",
            Password = "ReaderPass123!",
            IsAdmin = false,
        });

        // Login as non-admin and change password
        var readerClient = _factory.CreateClient();
        await readerClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "reader",
            Password = "ReaderPass123!",
        });

        var csrfResponse = await readerClient.GetAsync("/api/v1/auth/csrf");
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        readerClient.DefaultRequestHeaders.Add("X-MangaPlex-Csrf", csrf!.Token);

        await readerClient.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "ReaderPass123!",
            NewPassword = "ReaderNew123!",
        });

        // Re-login
        readerClient = _factory.CreateClient();
        await readerClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "reader",
            Password = "ReaderNew123!",
        });
        csrfResponse = await readerClient.GetAsync("/api/v1/auth/csrf");
        csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        readerClient.DefaultRequestHeaders.Add("X-MangaPlex-Csrf", csrf!.Token);

        var response = await readerClient.PutAsJsonAsync("/api/v1/operations/logging",
            new { level = "Debug" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PutLoggingLevel_InvalidLevel_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PutAsJsonAsync("/api/v1/operations/logging",
            new { level = "Trace" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutLoggingLevel_LogsChangeAtInformation()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        _factory.Sink.Clear();

        await client.PutAsJsonAsync("/api/v1/operations/logging",
            new { level = "Debug" });

        // The level-change event should appear at Information
        Assert.True(_factory.Sink.ContainsMessageAtLevel(LogEventLevel.Information, "Log level changed"),
            "Level change should be logged at Information");
    }

    [Fact]
    public async Task PutLoggingLevel_EmptyBody_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PutAsJsonAsync("/api/v1/operations/logging",
            new { level = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Per-category debug control (1.6.0 Lane E) ---

    [Fact]
    public async Task GetLoggingLevel_ReturnsCategories()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.GetAsync("/api/v1/operations/logging");

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Information", dto.GetProperty("level").GetString());

        var categories = dto.GetProperty("categories");
        Assert.Equal(JsonValueKind.Array, categories.ValueKind);
        var catList = categories.EnumerateArray().ToList();
        Assert.NotEmpty(catList);

        // Each category should default to Information + inherited
        foreach (var cat in catList)
        {
            Assert.Equal("Information", cat.GetProperty("level").GetString());
            Assert.True(cat.GetProperty("inherited").GetBoolean());
        }

        // The known categories should be present
        var names = catList.Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("Scanning", names);
        Assert.Contains("Media", names);
        Assert.Contains("Reading", names);
    }

    [Fact]
    public async Task PutLoggingLevel_Category_SetsCategoryAndDetachesFromGlobal()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Set Scanning to Debug via per-category override
        var putResponse = await client.PutAsJsonAsync("/api/v1/operations/logging",
            new
            {
                categories = new[]
                {
                    new { name = "Scanning", level = "Debug" },
                },
            });
        putResponse.EnsureSuccessStatusCode();
        var dto = await putResponse.Content.ReadFromJsonAsync<JsonElement>();

        // Global level should remain Information
        Assert.Equal("Information", dto.GetProperty("level").GetString());

        // Scanning should be Debug + not inherited
        var scanning = dto.GetProperty("categories")
            .EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "Scanning");
        Assert.Equal("Debug", scanning.GetProperty("level").GetString());
        Assert.False(scanning.GetProperty("inherited").GetBoolean());

        // Other categories should still be inherited
        var media = dto.GetProperty("categories")
            .EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "Media");
        Assert.True(media.GetProperty("inherited").GetBoolean());
    }

    [Fact]
    public async Task PutLoggingLevel_GlobalChange_SyncsInheritedCategories()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Set global to Debug
        await client.PutAsJsonAsync("/api/v1/operations/logging",
            new { level = "Debug" });

        // All categories should follow (inherited + Debug)
        var getResponse = await client.GetAsync("/api/v1/operations/logging");
        var dto = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var cat in dto.GetProperty("categories").EnumerateArray())
        {
            Assert.Equal("Debug", cat.GetProperty("level").GetString());
            Assert.True(cat.GetProperty("inherited").GetBoolean());
        }
    }

    [Fact]
    public async Task PutLoggingLevel_GlobalChange_DoesNotAffectExplicitCategory()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Set Scanning to Debug (explicit)
        await client.PutAsJsonAsync("/api/v1/operations/logging",
            new
            {
                categories = new[]
                {
                    new { name = "Scanning", level = "Debug" },
                },
            });

        // Now set global to Warning
        await client.PutAsJsonAsync("/api/v1/operations/logging",
            new { level = "Warning" });

        // Scanning should still be Debug (explicit, not synced)
        var getResponse = await client.GetAsync("/api/v1/operations/logging");
        var dto = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var scanning = dto.GetProperty("categories")
            .EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "Scanning");
        Assert.Equal("Debug", scanning.GetProperty("level").GetString());
        Assert.False(scanning.GetProperty("inherited").GetBoolean());

        // Media should be Warning (inherited, synced to global)
        var media = dto.GetProperty("categories")
            .EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "Media");
        Assert.Equal("Warning", media.GetProperty("level").GetString());
        Assert.True(media.GetProperty("inherited").GetBoolean());
    }

    [Fact]
    public async Task PutLoggingLevel_CategoryClear_ReturnsToInherit()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Set Scanning to Debug (explicit)
        await client.PutAsJsonAsync("/api/v1/operations/logging",
            new
            {
                categories = new[]
                {
                    new { name = "Scanning", level = "Debug" },
                },
            });

        // Clear Scanning (null level = inherit)
        var putResponse = await client.PutAsJsonAsync("/api/v1/operations/logging",
            new
            {
                categories = new[]
                {
                    new { name = "Scanning", level = (string?)null },
                },
            });
        putResponse.EnsureSuccessStatusCode();
        var dto = await putResponse.Content.ReadFromJsonAsync<JsonElement>();

        var scanning = dto.GetProperty("categories")
            .EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "Scanning");
        Assert.Equal("Information", scanning.GetProperty("level").GetString());
        Assert.True(scanning.GetProperty("inherited").GetBoolean());
    }

    [Fact]
    public async Task PutLoggingLevel_InvalidCategory_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PutAsJsonAsync("/api/v1/operations/logging",
            new
            {
                categories = new[]
                {
                    new { name = "Nonexistent", level = "Debug" },
                },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutLoggingLevel_CategoryIsolation_DebugOnlyFromOneCategory()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Set global to Warning (suppress all Debug), then enable Debug only
        // for Scanning.
        await client.PutAsJsonAsync("/api/v1/operations/logging",
            new
            {
                level = "Warning",
                categories = new[]
                {
                    new { name = "Scanning", level = "Debug" },
                },
            });

        _factory.Sink.Clear();

        // Emit a Debug event with SourceContext in the Scanning namespace
        var scanningLogger = Log.Logger.ForContext("SourceContext", "com.lifepixer.mangaplex.Server.Scanning.LibraryScanCoordinator");
        scanningLogger.Debug("Scanning debug test message");

        // Emit a Debug event with SourceContext in the Media namespace
        var mediaLogger = Log.Logger.ForContext("SourceContext", "com.lifepixer.mangaplex.Server.Media.MediaWorkerPool");
        mediaLogger.Debug("Media debug test message");

        // Only the Scanning Debug event should be captured
        Assert.True(_factory.Sink.ContainsMessage("Scanning debug test message"),
            "Scanning Debug event should be captured when Scanning is at Debug");
        Assert.False(_factory.Sink.ContainsMessage("Media debug test message"),
            "Media Debug event should NOT be captured when global is Warning and Media is inherited");
    }

    [Fact]
    public async Task PutLoggingLevel_CategoryChange_LogsAtInformation()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        _factory.Sink.Clear();

        await client.PutAsJsonAsync("/api/v1/operations/logging",
            new
            {
                categories = new[]
                {
                    new { name = "Scanning", level = "Debug" },
                },
            });

        Assert.True(_factory.Sink.ContainsMessageAtLevel(LogEventLevel.Information, "Log category"),
            "Category change should be logged at Information");
    }
}

/// <summary>
/// Standalone WebApplicationFactory that wraps Log.Logger with a CollectingSink
/// and provides admin login helpers. Follows the C00WebApplicationFactory pattern
/// since MangaPlexWebApplicationFactory is sealed.
/// </summary>
public sealed class LogLevelWebApplicationFactory : WebApplicationFactory<com.lifepixer.mangaplex.Server.Program>
{
    private readonly CollectingSink _sink = new();
    private readonly string _tempRoot;
    private readonly LoggingLevelSwitch _levelSwitch = new(LogEventLevel.Information);
    private readonly Dictionary<string, LoggingLevelSwitch> _categorySwitches = new();
    private Serilog.ILogger? _originalLogger;
    private readonly Dictionary<string, string?> _savedEnv = new();
    private HttpClient? _cachedAdminClient;

    public CollectingSink Sink => _sink;

    public LogLevelWebApplicationFactory()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mangaplex-loglevel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "data"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "cache"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "scratch"));

        // Create per-category switches matching the production catalog so
        // LogLevelSettingsService can mutate them. All default to Information.
        foreach (var (name, _) in com.lifepixer.mangaplex.Server.Logging.DebugCategories.All)
            _categorySwitches[name] = new LoggingLevelSwitch(LogEventLevel.Information);

        // Serialize env-set + host boot across parallel factories (see
        // TestHostBootGate). The storage env vars are process-global and
        // Program.Main reads them at the top of Main, before ConfigureWebHost
        // runs, so they must be ours at the moment of boot — otherwise a
        // parallel factory can overwrite them first and both hosts resolve
        // the same SQLite file, colliding on CREATE TABLE audit_events.
        using (TestHostBootGate.Acquire())
        {
            SetEnv("MangaPlex__Storage__DataRoot", Path.Combine(_tempRoot, "data"));
            SetEnv("MangaPlex__Storage__CacheRoot", Path.Combine(_tempRoot, "cache"));
            SetEnv("MangaPlex__Storage__ScratchRoot", Path.Combine(_tempRoot, "scratch"));
            SetEnv("Media__WorkerExecutablePath", "");
            SetEnv("MangaPlex__Security__RateLimit__Disabled", "true");

            // Force the host to boot now while our env vars are in effect.
            // The throwaway client is disposed at once; the host stays alive
            // until this factory is disposed.
            using var bootClient = CreateClient();
        }
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
            var workerHosted = services.FirstOrDefault(
                d => d.ImplementationType == typeof(MediaWorkerHostedService));
            if (workerHosted is not null)
                services.Remove(workerHosted);

            // Replace the LoggingLevelSwitch singleton with our test switch
            // so LogLevelSettingsService mutates the same switch that controls
            // the wrapper logger below.
            var existingSwitch = services.FirstOrDefault(
                d => d.ServiceType == typeof(LoggingLevelSwitch));
            if (existingSwitch is not null)
                services.Remove(existingSwitch);
            services.AddSingleton(_levelSwitch);

            // Replace the per-category switch dictionary so LogLevelSettingsService
            // mutates the same switches wired into the test logger below.
            var existingCatDict = services.FirstOrDefault(
                d => d.ServiceType == typeof(IReadOnlyDictionary<string, LoggingLevelSwitch>));
            if (existingCatDict is not null)
                services.Remove(existingCatDict);
            services.AddSingleton<IReadOnlyDictionary<string, LoggingLevelSwitch>>(_categorySwitches);

            _originalLogger = Log.Logger;
            var testLoggerConfig = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(_levelSwitch);
            foreach (var (name, prefix) in com.lifepixer.mangaplex.Server.Logging.DebugCategories.All)
                testLoggerConfig.MinimumLevel.Override(prefix, _categorySwitches[name]);
            Log.Logger = testLoggerConfig
                .WriteTo.Sink(_sink)
                .WriteTo.Logger(_originalLogger)
                .CreateLogger();
        });
    }

    public async Task<HttpClient> LoginAsAdminAsync(string password = "MangaPlex-Change-Me-Now!")
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

        var csrfResponse = await client.GetAsync("/api/v1/auth/csrf");
        csrfResponse.EnsureSuccessStatusCode();
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        Assert.NotNull(csrf);
        client.DefaultRequestHeaders.Add("X-MangaPlex-Csrf", csrf!.Token);

        return client;
    }

    public async Task<HttpClient> LoginAsAdminWithChangedPasswordAsync(
        string currentPassword = "MangaPlex-Change-Me-Now!",
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

        var freshClient = CreateClient();
        var loginResponse = await freshClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "admin",
            Password = newPassword,
        });
        loginResponse.EnsureSuccessStatusCode();

        var csrfResponse = await freshClient.GetAsync("/api/v1/auth/csrf");
        csrfResponse.EnsureSuccessStatusCode();
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        Assert.NotNull(csrf);
        freshClient.DefaultRequestHeaders.Add("X-MangaPlex-Csrf", csrf!.Token);

        _cachedAdminClient = freshClient;
        return freshClient;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_originalLogger is not null)
                Log.Logger = _originalLogger;

            foreach (var kvp in _savedEnv)
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);

            try { Directory.Delete(_tempRoot, true); } catch { }
        }
        base.Dispose(disposing);
    }
}
