namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using Xunit;

/// <summary>
/// HTTP tests for the per-library scan schedule (1.23.0): the admin-only
/// <c>PUT libraries/{id}/scan-schedule</c> endpoint, the additive
/// <c>scanSchedule</c> / <c>nextScheduledScanAt</c> DTO fields, and the
/// hosted scheduler starting a scan on its own in a running app.
/// </summary>
[Collection("HttpSerial")]
public sealed class LibraryScanScheduleHttpTests : IDisposable
{
    private readonly string _libRoot;

    public LibraryScanScheduleHttpTests()
    {
        // Outside the factory data root (app-root separation check).
        _libRoot = Path.Combine(Path.GetTempPath(), "mangapixer-schedlib-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series A"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_libRoot, true); } catch { }
    }

    private async Task<LibraryDto> RegisterAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = name,
            RootPath = _libRoot,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LibraryDto>(TestJson.Web))!;
    }

    [Fact]
    public async Task NewLibrary_ReportsDailyDefault_AndPresetsRoundTrip()
    {
        using var factory = new MangaPixerWebApplicationFactory();
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();
        var created = await RegisterAsync(client, "Schedule Test");

        // NULL stored schedule = daily default.
        Assert.Equal("1d", created.ScanSchedule);

        foreach (var preset in new[] { "off", "1h", "6h", "7d", "1d" })
        {
            var put = await client.PutAsJsonAsync(
                $"/api/v1/admin/libraries/{created.Id}/scan-schedule",
                new SetLibraryScanScheduleRequest { ScanSchedule = preset });
            put.EnsureSuccessStatusCode();
            Assert.Equal(preset, (await put.Content.ReadFromJsonAsync<LibraryDto>(TestJson.Web))!.ScanSchedule);

            var fetched = await client.GetFromJsonAsync<LibraryDto>($"/api/v1/admin/libraries/{created.Id}", TestJson.Web);
            Assert.Equal(preset, fetched!.ScanSchedule);
        }

        // Null clears back to the default.
        var clear = await client.PutAsJsonAsync(
            $"/api/v1/admin/libraries/{created.Id}/scan-schedule",
            new SetLibraryScanScheduleRequest { ScanSchedule = null });
        clear.EnsureSuccessStatusCode();
        Assert.Equal("1d", (await clear.Content.ReadFromJsonAsync<LibraryDto>(TestJson.Web))!.ScanSchedule);
    }

    [Fact]
    public async Task NextScheduledScanAt_IsNullWhenOffOrSchedulerDisabled()
    {
        // The default test factory runs with the scheduler disabled by configuration.
        using var factory = new MangaPixerWebApplicationFactory();
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();
        var created = await RegisterAsync(client, "Disabled Scheduler");
        Assert.Null(created.NextScheduledScanAt);

        using var enabled = MangaPixerWebApplicationFactory.WithExtraConfiguration(new Dictionary<string, string?>
        {
            ["MangaPixer:Scanning:Scheduler:Enabled"] = "true",
            ["MangaPixer:Scanning:Scheduler:StartupDelaySeconds"] = "3600",
        });
        var enabledClient = await enabled.LoginAsAdminWithChangedPasswordAsync();
        var lib = await RegisterAsync(enabledClient, "Enabled Scheduler");
        // Never scanned: due, but not before the first evaluation (boot + grace).
        Assert.NotNull(lib.NextScheduledScanAt);
        Assert.True(lib.NextScheduledScanAt > DateTimeOffset.UtcNow.AddMinutes(50));

        var off = await enabledClient.PutAsJsonAsync(
            $"/api/v1/admin/libraries/{lib.Id}/scan-schedule",
            new SetLibraryScanScheduleRequest { ScanSchedule = "off" });
        off.EnsureSuccessStatusCode();
        Assert.Null((await off.Content.ReadFromJsonAsync<LibraryDto>(TestJson.Web))!.NextScheduledScanAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("daily")]
    [InlineData("2h")]
    [InlineData("1D")]
    public async Task SetScanSchedule_InvalidToken_Returns400(string token)
    {
        using var factory = new MangaPixerWebApplicationFactory();
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();
        var created = await RegisterAsync(client, "Invalid Schedule");

        var put = await client.PutAsJsonAsync(
            $"/api/v1/admin/libraries/{created.Id}/scan-schedule",
            new SetLibraryScanScheduleRequest { ScanSchedule = token });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var error = await put.Content.ReadFromJsonAsync<ApiError>(TestJson.Web);
        Assert.Equal("invalid_scan_schedule", error!.Error);
    }

    [Fact]
    public async Task SetScanSchedule_UnknownLibrary_Returns404()
    {
        using var factory = new MangaPixerWebApplicationFactory();
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();

        var put = await client.PutAsJsonAsync(
            "/api/v1/admin/libraries/does-not-exist/scan-schedule",
            new SetLibraryScanScheduleRequest { ScanSchedule = "1h" });
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task SetScanSchedule_AsNonAdmin_Returns403_AndCatalogDtoOmitsSchedule()
    {
        using var factory = new MangaPixerWebApplicationFactory();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var created = await RegisterAsync(admin, "Auth Schedule");

        var createUser = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "schedreader",
            Password = "ReaderPass123!",
            IsAdmin = false,
        });
        createUser.EnsureSuccessStatusCode();
        var reader = await LoginAndChangePasswordAsync(factory, "schedreader", "ReaderPass123!", "ReaderNewPass123!");

        var put = await reader.PutAsJsonAsync(
            $"/api/v1/admin/libraries/{created.Id}/scan-schedule",
            new SetLibraryScanScheduleRequest { ScanSchedule = "off" });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        // The admin-only fields stay out of the general catalog listing.
        var libraries = await admin.GetFromJsonAsync<List<LibraryDto>>("/api/v1/libraries", TestJson.Web);
        var listed = Assert.Single(libraries!, l => l.Id == created.Id);
        Assert.Null(listed.ScanSchedule);
        Assert.Null(listed.NextScheduledScanAt);
    }

    [Fact(Timeout = 60000)]
    public async Task HostedScheduler_ScansANeverScannedLibrary_WithoutAnAdminTrigger()
    {
        using var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(new Dictionary<string, string?>
        {
            ["MangaPixer:Scanning:Scheduler:Enabled"] = "true",
            ["MangaPixer:Scanning:Scheduler:StartupDelaySeconds"] = "0",
            ["MangaPixer:Scanning:Scheduler:TickSeconds"] = "1",
        });
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();
        var created = await RegisterAsync(client, "Scheduled");

        List<ScanRunDto>? scans = null;
        for (var i = 0; i < 200; i++)
        {
            scans = await client.GetFromJsonAsync<List<ScanRunDto>>($"/api/v1/admin/libraries/{created.Id}/scans", TestJson.Web);
            if (scans!.Any(s => s.Status == "completed"))
                break;
            await Task.Delay(100);
        }
        var completed = Assert.Single(scans!);
        Assert.Equal("completed", completed.Status);

        // Now the next scan is one day after that completed scan.
        var fetched = await client.GetFromJsonAsync<LibraryDto>($"/api/v1/admin/libraries/{created.Id}", TestJson.Web);
        Assert.NotNull(fetched!.LastScanCompleted);
        Assert.Equal(fetched.LastScanCompleted!.Value.AddDays(1), fetched.NextScheduledScanAt);
    }

    private static async Task<HttpClient> LoginAndChangePasswordAsync(
        MangaPixerWebApplicationFactory factory, string username, string oldPassword, string newPassword)
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = oldPassword }))
            .EnsureSuccessStatusCode();
        var csrf = await client.GetFromJsonAsync<CsrfTokenDto>("/api/v1/auth/csrf", TestJson.Web);
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
        (await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = oldPassword,
            NewPassword = newPassword,
        })).EnsureSuccessStatusCode();

        var fresh = factory.CreateClient();
        (await fresh.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = newPassword }))
            .EnsureSuccessStatusCode();
        var freshCsrf = await fresh.GetFromJsonAsync<CsrfTokenDto>("/api/v1/auth/csrf", TestJson.Web);
        fresh.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", freshCsrf!.Token);
        return fresh;
    }
}
