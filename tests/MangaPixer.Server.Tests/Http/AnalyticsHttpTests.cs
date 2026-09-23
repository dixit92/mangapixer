namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using Xunit;

/// <summary>
/// HTTP integration tests (1.22.0 lane E) for the admin analytics endpoints:
/// <c>GET /api/v1/admin/analytics/overview</c> and
/// <c>GET /api/v1/admin/analytics/users</c>. Verifies admin-only gating,
/// response shape, and — the privacy invariant this feature exists to
/// respect — that no title, item name, or path string ever appears in the
/// response body.
/// </summary>
[Collection("HttpSerial")]
public sealed class AnalyticsHttpTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory;

    public AnalyticsHttpTests()
    {
        _factory = new MangaPixerWebApplicationFactory();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task GetOverview_Admin_ReturnsCounts()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await admin.GetAsync("/api/v1/admin/analytics/overview");
        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dto.TryGetProperty("libraryCount", out _));
        Assert.True(dto.TryGetProperty("userCount", out var userCount));
        Assert.True(userCount.GetInt32() >= 1); // the seeded admin
        Assert.True(dto.TryGetProperty("activeSessionCount", out _));
        Assert.True(dto.TryGetProperty("generatedAt", out _));
    }

    [Fact]
    public async Task GetOverview_NonAdmin_Returns403()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var reader = await CreateAndLoginReaderAsync(admin, "overviewreader");

        var response = await reader.GetAsync("/api/v1/admin/analytics/overview");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetUsers_Admin_IncludesOwnRow()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await admin.GetAsync("/api/v1/admin/analytics/users");
        response.EnsureSuccessStatusCode();

        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        var usernames = rows.EnumerateArray()
            .Select(r => r.GetProperty("username").GetString())
            .ToList();
        Assert.Contains("admin", usernames);

        var adminRow = rows.EnumerateArray().First(r => r.GetProperty("username").GetString() == "admin");
        Assert.True(adminRow.GetProperty("isAdmin").GetBoolean());
        Assert.True(adminRow.TryGetProperty("chaptersCompleted", out _));
        Assert.True(adminRow.TryGetProperty("chaptersInProgress", out _));
        Assert.True(adminRow.TryGetProperty("bookmarkCount", out _));
        Assert.True(adminRow.TryGetProperty("favoriteCount", out _));
        Assert.True(adminRow.TryGetProperty("lastReadingActivityAt", out _));
    }

    [Fact]
    public async Task GetUsers_NonAdmin_Returns403()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var reader = await CreateAndLoginReaderAsync(admin, "usersreader");

        var response = await reader.GetAsync("/api/v1/admin/analytics/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Responses_NeverContainPathOrLibraryDisplayNameStrings()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Register a real library whose name/root would leak if any endpoint
        // echoed source strings instead of pure counts.
        var tempRoot = Path.Combine(Path.GetTempPath(), "mangapixer-analytics-http-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        try
        {
            var register = await admin.PostAsJsonAsync("/api/v1/admin/libraries",
                new RegisterLibraryRequest { DisplayName = "SecretLibraryNameXYZ", RootPath = tempRoot });
            register.EnsureSuccessStatusCode();

            var overviewResponse = await admin.GetAsync("/api/v1/admin/analytics/overview");
            var overviewBody = await overviewResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SecretLibraryNameXYZ", overviewBody);
            Assert.DoesNotContain(tempRoot, overviewBody);

            var usersResponse = await admin.GetAsync("/api/v1/admin/analytics/users");
            var usersBody = await usersResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SecretLibraryNameXYZ", usersBody);
            Assert.DoesNotContain(tempRoot, usersBody);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static async Task<string> CreateUserAsync(HttpClient admin, string username)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = username,
            Password = "TargetPass123!",
            IsAdmin = false,
        });
        response.EnsureSuccessStatusCode();
        return username;
    }

    private async Task<HttpClient> CreateAndLoginReaderAsync(HttpClient admin, string username)
    {
        await CreateUserAsync(admin, username);

        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = username,
            Password = "TargetPass123!",
        });
        var csrfResponse = await client.GetAsync("/api/v1/auth/csrf");
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        Assert.NotNull(csrf);
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);

        await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "TargetPass123!",
            NewPassword = "TargetPassNew123!",
        });

        var freshClient = _factory.CreateClient();
        await freshClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = username,
            Password = "TargetPassNew123!",
        });
        csrfResponse = await freshClient.GetAsync("/api/v1/auth/csrf");
        csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        Assert.NotNull(csrf);
        freshClient.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);

        return freshClient;
    }
}
