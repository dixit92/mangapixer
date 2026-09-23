namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP (WebApplicationFactory) tests for the backup settings + custom backup
/// location (1.22.0) through the public operations API: shapes and sources,
/// validation codes, configuration-managed 409s, the current-password gate
/// (incl. rate limiting), CSRF, audit rows, backups + list + restore from a
/// custom folder, fail-loud readiness (Degraded), privacy (no location / data
/// root in status, list, restore bodies or in the log file), and the removal
/// of the orphan <c>POST /api/v1/operations/backup {path}</c>.
///
/// "HttpSerial" because every host boot reassigns the process-global Serilog logger.
/// </summary>
[Collection("HttpSerial")]
public sealed class BackupSettingsHttpTests : IDisposable
{
    private const string AdminPassword = "TestPassword123!";
    private const string SettingsUrl = "/api/v1/operations/backups/settings";

    private readonly MangaPixerWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static object CustomLocation(string location, string? password = AdminPassword, bool validateOnly = false) => new
    {
        location = new { mode = "custom", customLocation = location },
        currentPassword = password,
        validateOnly,
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("error").GetString();

    [Fact]
    public async Task GetSettings_Defaults_WithSources_AndNoDataRoot()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.GetAsync(SettingsUrl);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var dto = JsonDocument.Parse(body).RootElement;
        Assert.True(dto.GetProperty("enabled").GetBoolean());
        Assert.Equal("default", dto.GetProperty("enabledSource").GetString());
        Assert.Equal(24, dto.GetProperty("intervalHours").GetDouble());
        Assert.Equal(7, dto.GetProperty("retentionCount").GetInt32());
        Assert.Equal("default", dto.GetProperty("locationKind").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("customLocation").ValueKind);
        Assert.True(dto.GetProperty("locationChangeAllowed").GetBoolean());
        Assert.True(dto.TryGetProperty("platform", out _));
        Assert.DoesNotContain(_factory.DataRoot, body);
    }

    [Fact]
    public async Task PutSettings_AppliesLive_AndAudits()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PutAsJsonAsync(SettingsUrl, new { enabled = false, intervalHours = 12, retentionCount = 3 });

        response.EnsureSuccessStatusCode();
        var dto = (await JsonAsync(response)).GetProperty("settings");
        Assert.False(dto.GetProperty("enabled").GetBoolean());
        Assert.Equal("settings", dto.GetProperty("retentionCountSource").GetString());

        // The status endpoint reflects the change without a restart.
        var status = await JsonAsync(await client.GetAsync("/api/v1/operations/backups"));
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(12, status.GetProperty("intervalHours").GetDouble());
        Assert.Equal(3, status.GetProperty("retentionCount").GetInt32());
        Assert.Equal("default", status.GetProperty("locationKind").GetString());

        Assert.Equal(1, await CountAuditAsync(AuditActions.BackupSettingsChanged, AuditResults.Success));
    }

    [Theory]
    [InlineData(0.5, null, "invalid_interval")]
    [InlineData(721.0, null, "invalid_interval")]
    [InlineData(null, 0, "invalid_retention")]
    [InlineData(null, 101, "invalid_retention")]
    public async Task PutSettings_OutOfRange_Returns400(double? hours, int? retention, string code)
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PutAsJsonAsync(SettingsUrl, new { intervalHours = hours, retentionCount = retention });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Location_RequiresTheCurrentPassword()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var target = Path.Combine(_factory.TempRoot, "custom-backups");

        var missing = await client.PutAsJsonAsync(SettingsUrl, CustomLocation(target, password: null));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("current_password_required", await ErrorCodeAsync(missing));

        var wrong = await client.PutAsJsonAsync(SettingsUrl, CustomLocation(target, password: "wrong-password"));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal("reauthentication_failed", await ErrorCodeAsync(wrong));

        // validateOnly shares the gate (no password-less probe endpoint).
        var probe = await client.PutAsJsonAsync(SettingsUrl, CustomLocation(target, password: "wrong-password", validateOnly: true));
        Assert.Equal(HttpStatusCode.Forbidden, probe.StatusCode);

        Assert.False(Directory.Exists(target));
        Assert.Equal(3, await CountAuditAsync(AuditActions.BackupLocationChanged, AuditResults.Failure));
    }

    [Fact]
    public async Task Location_ValidationCodes()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        async Task<string?> CodeFor(string location)
        {
            var response = await client.PutAsJsonAsync(SettingsUrl, CustomLocation(location));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(_factory.DataRoot, body); // generic messages, never a path
            return JsonDocument.Parse(body).RootElement.GetProperty("error").GetString();
        }

        Assert.Equal(BackupLocationCodes.NotAbsolute, await CodeFor("relative/backups"));
        Assert.Equal(BackupLocationCodes.Invalid, await CodeFor(Path.Combine(_factory.TempRoot, "a", "..", "b")));
        Assert.Equal(BackupLocationCodes.OverlapsProtected, await CodeFor(_factory.DataRoot));
        Assert.Equal(BackupLocationCodes.OverlapsProtected, await CodeFor(Path.Combine(_factory.DataRoot, "backups-2")));
        Assert.Equal(BackupLocationCodes.OverlapsProtected, await CodeFor(Path.Combine(_factory.CacheRoot, "b")));
        Assert.Equal(BackupLocationCodes.OverlapsProtected, await CodeFor(_factory.TempRoot)); // ancestor of the data root
        Assert.Equal(BackupLocationCodes.ParentMissing, await CodeFor(Path.Combine(_factory.TempRoot, "no", "such", "parent")));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(BackupLocationCodes.Forbidden, await CodeFor("/etc/mangapixer-backups"));
    }

    [Fact]
    public async Task CustomLocation_EndToEnd_BackupListRestore_WithoutLeakingTheLocation()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var target = Path.Combine(_factory.TempRoot, "custom-backups");

        // Test first (validateOnly): nothing is created or changed.
        var test = await client.PutAsJsonAsync(SettingsUrl, CustomLocation(target, validateOnly: true));
        test.EnsureSuccessStatusCode();
        var testDto = await JsonAsync(test);
        Assert.True(testDto.GetProperty("validateOnly").GetBoolean());
        Assert.True(testDto.GetProperty("willCreate").GetBoolean());
        Assert.False(Directory.Exists(target));

        var save = await client.PutAsJsonAsync(SettingsUrl, CustomLocation(target));
        save.EnsureSuccessStatusCode();
        var saved = (await JsonAsync(save)).GetProperty("settings");
        Assert.Equal("custom", saved.GetProperty("locationKind").GetString());
        Assert.Equal(target, saved.GetProperty("customLocation").GetString());
        Assert.Equal("ok", saved.GetProperty("locationStatus").GetString());
        Assert.True(File.Exists(Path.Combine(target, BackupLocationValidator.MarkerFileName)));

        // Back up now: the snapshot lands in the custom folder.
        var run = await client.PostAsync("/api/v1/operations/backups/rotating", content: null);
        run.EnsureSuccessStatusCode();
        var runBody = await run.Content.ReadAsStringAsync();
        var fileName = JsonDocument.Parse(runBody).RootElement.GetProperty("lastBackupFileName").GetString();
        Assert.True(File.Exists(Path.Combine(target, fileName!)));
        Assert.Equal("custom", JsonDocument.Parse(runBody).RootElement.GetProperty("locationKind").GetString());

        var list = await client.GetAsync("/api/v1/operations/backups/files");
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.Contains(fileName!, listBody);

        var restore = await client.PostAsJsonAsync("/api/v1/operations/backups/restore", new { fileName });
        Assert.Equal(HttpStatusCode.Accepted, restore.StatusCode);
        var restoreBody = await restore.Content.ReadAsStringAsync();
        var preRestore = JsonDocument.Parse(restoreBody).RootElement.GetProperty("preRestoreBackupFileName").GetString();
        Assert.True(File.Exists(Path.Combine(_factory.DataRoot, "backups", preRestore!)),
            "The pre-restore safety snapshot stays in the local data folder.");

        var statusBody = await (await client.GetAsync("/api/v1/operations/backups")).Content.ReadAsStringAsync();
        foreach (var body in new[] { runBody, listBody, restoreBody, statusBody })
        {
            Assert.DoesNotContain(target, body);
            Assert.DoesNotContain(_factory.DataRoot, body);
        }

        Assert.Equal(1, await CountAuditAsync(AuditActions.BackupLocationChanged, AuditResults.Success));
    }

    [Fact]
    public async Task UnavailableLocation_FailsLoud_ReadinessDegraded_AndLogsNoPath()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var target = Path.Combine(_factory.TempRoot, "nas-share");
        (await client.PutAsJsonAsync(SettingsUrl, CustomLocation(target))).EnsureSuccessStatusCode();
        Assert.Equal("Healthy", await (await client.GetAsync("/health/ready")).Content.ReadAsStringAsync());

        // The share "unmounts": its marker is gone.
        File.Delete(Path.Combine(target, BackupLocationValidator.MarkerFileName));

        var run = await client.PostAsync("/api/v1/operations/backups/rotating", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, run.StatusCode);
        Assert.Equal("backup_location_unavailable", await ErrorCodeAsync(run));
        Assert.Empty(Directory.EnumerateFiles(target, "rotating-*.db"));

        var status = await JsonAsync(await client.GetAsync("/api/v1/operations/backups"));
        Assert.Equal("unavailable", status.GetProperty("locationStatus").GetString());
        Assert.Equal(BackupLocationCodes.Unavailable, status.GetProperty("lastFailureCode").GetString());

        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Degraded", await ready.Content.ReadAsStringAsync());

        Assert.Equal(1, await CountAuditAsync(AuditActions.BackupLocationUnavailable, AuditResults.Failure));

        // Nothing written to the log file names the location.
        var logs = Path.Combine(_factory.DataRoot, "logs");
        foreach (var file in Directory.EnumerateFiles(logs))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();
            Assert.DoesNotContain(target, text);
        }
    }

    [Fact]
    public async Task OrphanArbitraryPathBackupEndpoint_IsGone()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var target = Path.Combine(_factory.TempRoot, "exfil.db");

        var response = await client.PostAsJsonAsync("/api/v1/operations/backup", new { path = target });

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"Expected 404/405, got {(int)response.StatusCode}.");
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task PutSettings_WithoutCsrf_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var csrf = client.DefaultRequestHeaders.GetValues("X-MangaPixer-Csrf").ToList();
        client.DefaultRequestHeaders.Remove("X-MangaPixer-Csrf");
        try
        {
            var response = await client.PutAsJsonAsync(SettingsUrl, new { retentionCount = 3 });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf);
        }
    }

    [Fact]
    public async Task Settings_NonAdmin_Returns403()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "reader",
            Password = "ReaderPass123!",
            IsAdmin = false,
        });

        // The new user must change the forced password first, then sign in again.
        var first = _factory.CreateClient();
        (await first.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "reader", Password = "ReaderPass123!" }))
            .EnsureSuccessStatusCode();
        var csrf = await first.GetFromJsonAsync<CsrfTokenDto>("/api/v1/auth/csrf");
        first.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
        (await first.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "ReaderPass123!",
            NewPassword = "ReaderNew123!",
        })).EnsureSuccessStatusCode();

        var reader = _factory.CreateClient();
        (await reader.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "reader", Password = "ReaderNew123!" }))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(SettingsUrl)).StatusCode);
    }

    private async Task<int> CountAuditAsync(string action, string result)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await db.AuditEvents.CountAsync(e => e.Action == action && e.Result == result);
    }
}

/// <summary>
/// HTTP tests for the operator side of the backup settings: configuration-
/// pinned fields are read-only (409) and a configured location is initialized
/// and used at startup; and the location password gate is rate-limited.
/// </summary>
[Collection("HttpSerial")]
public sealed class BackupSettingsConfigurationHttpTests
{
    private const string SettingsUrl = "/api/v1/operations/backups/settings";

    [Fact]
    public async Task ConfiguredFields_AreReadOnly_AndTheConfiguredLocationIsUsed()
    {
        var pinned = Path.Combine(Path.GetTempPath(), "mangapixer-pinned-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(pinned);
        try
        {
            using var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(new Dictionary<string, string?>
            {
                ["MangaPixer:Backups:RetentionCount"] = "3",
                ["MangaPixer:Backups:Location"] = pinned,
            });
            var client = await factory.LoginAsAdminWithChangedPasswordAsync();

            var dto = await WaitForLocationStatusAsync(client);
            Assert.Equal("configuration", dto.GetProperty("retentionCountSource").GetString());
            Assert.Equal("configuration", dto.GetProperty("locationSource").GetString());
            Assert.Equal("custom", dto.GetProperty("locationKind").GetString());
            Assert.False(dto.GetProperty("locationChangeAllowed").GetBoolean());
            Assert.Equal("ok", dto.GetProperty("locationStatus").GetString());

            var retention = await client.PutAsJsonAsync(SettingsUrl, new { retentionCount = 5 });
            Assert.Equal(HttpStatusCode.Conflict, retention.StatusCode);
            Assert.Equal("managed_by_configuration", (await retention.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

            var location = await client.PutAsJsonAsync(SettingsUrl, new
            {
                location = new { mode = "default" },
                currentPassword = "TestPassword123!",
            });
            Assert.Equal(HttpStatusCode.Conflict, location.StatusCode);

            // Fields that are not configured stay editable.
            (await client.PutAsJsonAsync(SettingsUrl, new { intervalHours = 48 })).EnsureSuccessStatusCode();

            var run = await client.PostAsync("/api/v1/operations/backups/rotating", content: null);
            run.EnsureSuccessStatusCode();
            Assert.Single(Directory.EnumerateFiles(pinned, "rotating-*.db"));
            Assert.True(File.Exists(Path.Combine(pinned, BackupLocationValidator.MarkerFileName)));
        }
        finally
        {
            try { Directory.Delete(pinned, true); } catch { }
        }
    }

    [Fact]
    public async Task WrongLocationPasswords_CountAgainstTheLoginRateLimiter()
    {
        using var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(
            new Dictionary<string, string?>(), rateLimitDisabled: false);
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();
        var body = new
        {
            location = new { mode = "custom", customLocation = Path.Combine(factory.TempRoot, "b") },
            currentPassword = "wrong-password",
            validateOnly = true,
        };

        // Default per-user limit is 5 failures per window.
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(SettingsUrl, body)).StatusCode);

        var limited = await client.PutAsJsonAsync(SettingsUrl, body with { currentPassword = "TestPassword123!" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
    }

    private static async Task<JsonElement> WaitForLocationStatusAsync(HttpClient client)
    {
        // The startup location check runs in the backup hosted service.
        JsonElement dto = default;
        for (var i = 0; i < 50; i++)
        {
            dto = await client.GetFromJsonAsync<JsonElement>(SettingsUrl);
            if (dto.GetProperty("locationStatus").GetString() != "unknown")
                return dto;
            await Task.Delay(100);
        }
        return dto;
    }
}
