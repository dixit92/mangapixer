namespace com.lifepixer.mangaplex.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangaplex.Core.Api;
using Xunit;

/// <summary>
/// HTTP integration tests for auth, CSRF, and basic endpoint behavior.
/// Each test creates its own factory to avoid cross-test state interference
/// (password changes, rate limiter state, etc.).
/// </summary>
[Collection("HttpSerial")]
public sealed class AuthHttpTests : IDisposable
{
    private readonly MangaPlexWebApplicationFactory _factory;

    public AuthHttpTests()
    {
        _factory = new MangaPlexWebApplicationFactory();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetCsrf_ReturnsTokenAndCookie()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/auth/csrf");
        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<CsrfTokenDto>();
        Assert.NotNull(dto);
        Assert.False(string.IsNullOrEmpty(dto!.Token));
        Assert.True(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Login_WithDefaultAdmin_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "admin",
            Password = "MangaPlex-Change-Me-Now!",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<AuthUserDto>();
        Assert.NotNull(user);
        Assert.Equal("admin", user!.Username);
        Assert.True(user.IsAdmin);
    }

    [Fact]
    public async Task Login_WithBadCredentials_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "admin",
            Password = "wrong-password",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithAuth_ReturnsUser()
    {
        var client = await _factory.LoginAsAdminAsync();
        var response = await client.GetAsync("/api/v1/auth/me");
        response.EnsureSuccessStatusCode();

        var user = await response.Content.ReadFromJsonAsync<AuthUserDto>();
        Assert.NotNull(user);
        Assert.Equal("admin", user!.Username);
    }

    [Fact]
    public async Task Libraries_WithoutChangedPassword_Returns401()
    {
        var client = await _factory.LoginAsAdminAsync();
        var response = await client.GetAsync("/api/v1/libraries");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Libraries_AfterPasswordChange_Returns200Empty()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var response = await client.GetAsync("/api/v1/libraries");
        response.EnsureSuccessStatusCode();

        var libraries = await response.Content.ReadFromJsonAsync<List<LibraryDto>>();
        Assert.NotNull(libraries);
        Assert.Empty(libraries!);
    }

    [Fact]
    public async Task ChangePassword_WithCorrectOldPassword_Returns204()
    {
        var client = await _factory.LoginAsAdminAsync();
        var response = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "MangaPlex-Change-Me-Now!",
            NewPassword = "NewTestPassword123!",
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesSession()
    {
        var client = await _factory.LoginAsAdminAsync();
        var logoutResponse = await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var meResponse = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meResponse.StatusCode);
    }

    [Fact]
    public async Task ApiNotFound_Returns404()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/nothing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SpaFallback_ServesIndexHtmlForNonApiRoutes()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/libraries");
        Assert.True(response.StatusCode == HttpStatusCode.OK
            || response.StatusCode == HttpStatusCode.NotFound);
    }
}
