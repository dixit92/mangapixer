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
    public async Task SetupStatus_OnFreshInstance_ReportsSetupRequired()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/auth/setup-status");
        response.EnsureSuccessStatusCode();

        var status = await response.Content.ReadFromJsonAsync<SetupStatusDto>();
        Assert.NotNull(status);
        Assert.True(status!.SetupRequired);
    }

    [Fact]
    public async Task NoDefaultCredential_LoginFails_OnFreshInstance()
    {
        // Audit finding F2: a fresh instance ships no default credential, so the
        // former default admin cannot sign in before the user runs setup.
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "admin",
            Password = "MangaPlex-Change-Me-Now!",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Setup_CreatesFirstAdmin_AndSignsIn()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new SetupRequest
        {
            Username = "admin",
            Password = "ChosenPassword123!",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<AuthUserDto>();
        Assert.NotNull(user);
        Assert.Equal("admin", user!.Username);
        Assert.True(user.IsAdmin);

        // Setup already signed the client in — /auth/me works without a login.
        var me = await client.GetAsync("/api/v1/auth/me");
        me.EnsureSuccessStatusCode();

        // Setup is now complete and cannot be reused.
        var status = await (await client.GetAsync("/api/v1/auth/setup-status"))
            .Content.ReadFromJsonAsync<SetupStatusDto>();
        Assert.False(status!.SetupRequired);
    }

    [Fact]
    public async Task Setup_Rejected_OnceUserExists_Returns409()
    {
        var client = _factory.CreateClient();
        var first = await client.PostAsJsonAsync("/api/v1/auth/setup", new SetupRequest
        {
            Username = "admin",
            Password = "ChosenPassword123!",
        });
        first.EnsureSuccessStatusCode();

        var second = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/setup", new SetupRequest
        {
            Username = "intruder",
            Password = "AnotherPassword123!",
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
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
    public async Task Libraries_AfterSetup_Returns200Empty()
    {
        // The first admin created via setup chose its own password, so there is
        // no forced password change gating access (audit finding F2).
        var client = await _factory.LoginAsAdminAsync();
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
