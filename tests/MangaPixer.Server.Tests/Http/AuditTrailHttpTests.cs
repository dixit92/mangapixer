namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using Xunit;

/// <summary>
/// HTTP integration tests (1.18.0) for the administrative audit trail. The
/// audit store predates this feature but was write-only; these exercise the new
/// read path plus the write points wired at key admin actions:
/// - A password reset records a "user.password.reset" success event.
/// - A user delete records a "user.delete" success event.
/// - GET /api/v1/admin/audit returns the events newest-first, paged, with the
///   acting admin's user name resolved and no paths/secrets.
/// - Non-admin users get 403.
/// </summary>
[Collection("HttpSerial")]
public sealed class AuditTrailHttpTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory;

    public AuditTrailHttpTests()
    {
        _factory = new MangaPixerWebApplicationFactory();
    }

    public void Dispose() => _factory.Dispose();

    private static async Task<string> CreateUserAsync(HttpClient admin, string username)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = username,
            Password = "TargetPass123!",
            IsAdmin = false,
        });
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        return dto.GetProperty("user").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task PasswordReset_RecordsAuditEvent_ReadableThroughTrail()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var userId = await CreateUserAsync(admin, "resetme");

        var reset = await admin.PostAsync($"/api/v1/admin/users/{userId}/reset-password", content: null);
        reset.EnsureSuccessStatusCode();

        var response = await admin.GetAsync("/api/v1/admin/audit?page=1&pageSize=50");
        response.EnsureSuccessStatusCode();
        var trail = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(trail.GetProperty("totalCount").GetInt32() >= 1);
        Assert.Equal(1, trail.GetProperty("page").GetInt32());
        Assert.Equal(50, trail.GetProperty("pageSize").GetInt32());

        var items = trail.GetProperty("items");
        var reset_event = items.EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("action").GetString() == "user.password.reset");
        Assert.Equal(JsonValueKind.Object, reset_event.ValueKind);
        Assert.Equal("success", reset_event.GetProperty("result").GetString());
        Assert.Equal("admin", reset_event.GetProperty("actorUserName").GetString());
        Assert.NotEqual(JsonValueKind.Null, reset_event.GetProperty("targetUserId").ValueKind);
    }

    [Fact]
    public async Task UserDelete_RecordsAuditEvent()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var userId = await CreateUserAsync(admin, "deleteme");

        var del = await admin.DeleteAsync($"/api/v1/admin/users/{userId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var response = await admin.GetAsync("/api/v1/admin/audit");
        response.EnsureSuccessStatusCode();
        var trail = await response.Content.ReadFromJsonAsync<JsonElement>();
        var actions = trail.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("action").GetString())
            .ToList();
        Assert.Contains("user.delete", actions);
    }

    [Fact]
    public async Task LoggingChange_RecordsAuditEvent()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var put = await admin.PutAsJsonAsync("/api/v1/operations/logging",
            new UpdateLogLevelRequest { Level = "Information" });
        put.EnsureSuccessStatusCode();

        var response = await admin.GetAsync("/api/v1/admin/audit");
        response.EnsureSuccessStatusCode();
        var trail = await response.Content.ReadFromJsonAsync<JsonElement>();
        var actions = trail.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("action").GetString())
            .ToList();
        Assert.Contains("logging.change", actions);
    }

    [Fact]
    public async Task GetAudit_EmptyTrail_ReturnsEmptyPage()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await admin.GetAsync("/api/v1/admin/audit");
        response.EnsureSuccessStatusCode();
        var trail = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, trail.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, trail.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetAudit_NonAdmin_Returns403()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "reader",
            Password = "ReaderPass123!",
            IsAdmin = false,
        });
        var reader = await LoginReaderAsync("reader");

        var response = await reader.GetAsync("/api/v1/admin/audit");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<HttpClient> LoginReaderAsync(string username)
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = username,
            Password = "ReaderPass123!",
        });
        var csrfResponse = await client.GetAsync("/api/v1/auth/csrf");
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        Assert.NotNull(csrf);
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);

        await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "ReaderPass123!",
            NewPassword = "ReaderNew123!",
        });

        var freshClient = _factory.CreateClient();
        await freshClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = username,
            Password = "ReaderNew123!",
        });
        csrfResponse = await freshClient.GetAsync("/api/v1/auth/csrf");
        csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        Assert.NotNull(csrf);
        freshClient.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);

        return freshClient;
    }
}
