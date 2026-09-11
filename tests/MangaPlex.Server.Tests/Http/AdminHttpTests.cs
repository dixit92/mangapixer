namespace com.lifepixer.mangaplex.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangaplex.Core.Api;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP integration tests for admin endpoints: library registration,
/// user management, grants, and scan triggering.
/// </summary>
[Collection("HttpSerial")]
public sealed class AdminHttpTests : IDisposable
{
    private readonly MangaPlexWebApplicationFactory _factory;
    private readonly string _libRoot;

    public AdminHttpTests()
    {
        _factory = new MangaPlexWebApplicationFactory();
        // Library root must be OUTSIDE the data root to avoid the
        // app-root separation check in LibraryRegistrationService.
        _libRoot = Path.Combine(Path.GetTempPath(), "mangaplex-lib-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_libRoot);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_libRoot, true); } catch { }
    }

    /// <summary>
    /// Logs in as a user with ForcePasswordChange set, changes the password,
    /// and re-logs in with the new password. Returns an authenticated client.
    /// </summary>
    private async Task<HttpClient> LoginAndChangePasswordAsync(string username, string oldPassword, string newPassword)
    {
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = username,
            Password = oldPassword,
        });
        loginResponse.EnsureSuccessStatusCode();

        var csrfResponse = await client.GetAsync("/api/v1/auth/csrf");
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        client.DefaultRequestHeaders.Add("X-MangaPlex-Csrf", csrf!.Token);

        var changeResponse = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = oldPassword,
            NewPassword = newPassword,
        });
        changeResponse.EnsureSuccessStatusCode();

        var freshClient = _factory.CreateClient();
        var reLoginResponse = await freshClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = username,
            Password = newPassword,
        });
        reLoginResponse.EnsureSuccessStatusCode();

        var freshCsrf = await freshClient.GetAsync("/api/v1/auth/csrf");
        var freshToken = await freshCsrf.Content.ReadFromJsonAsync<CsrfTokenDto>();
        freshClient.DefaultRequestHeaders.Add("X-MangaPlex-Csrf", freshToken!.Token);

        return freshClient;
    }

    [Fact]
    public async Task Admin_WithoutAdminRole_Returns403()
    {
        // Login as admin, change password, then create a non-admin user
        var adminClient = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var createUserResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "reader1",
            Password = "ReaderPass123!",
            IsAdmin = false,
        });
        createUserResponse.EnsureSuccessStatusCode();

        // Login as the non-admin user (forced password change is set)
        var readerClient = _factory.CreateClient();
        var loginResponse = await readerClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "reader1",
            Password = "ReaderPass123!",
        });
        loginResponse.EnsureSuccessStatusCode();

        // Get CSRF
        var csrfResponse = await readerClient.GetAsync("/api/v1/auth/csrf");
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        readerClient.DefaultRequestHeaders.Add("X-MangaPlex-Csrf", csrf!.Token);

        // Change password to clear ForcePasswordChange
        var changeResponse = await readerClient.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "ReaderPass123!",
            NewPassword = "ReaderNewPass123!",
        });
        changeResponse.EnsureSuccessStatusCode();

        // Re-login with new password
        readerClient = _factory.CreateClient();
        loginResponse = await readerClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "reader1",
            Password = "ReaderNewPass123!",
        });
        loginResponse.EnsureSuccessStatusCode();

        // Try to access admin endpoint — should be 403 Forbidden
        var adminResponse = await readerClient.GetAsync("/api/v1/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, adminResponse.StatusCode);
    }

    [Fact]
    public async Task RegisterLibrary_WithValidRoot_Returns201()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "Test Library",
            RootPath = _libRoot,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var library = await response.Content.ReadFromJsonAsync<LibraryDto>();
        Assert.NotNull(library);
        Assert.Equal("Test Library", library!.Name);
        Assert.False(string.IsNullOrEmpty(library.Id));
    }

    [Fact]
    public async Task RegisterLibrary_WithNonExistentRoot_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "Missing",
            RootPath = Path.Combine(_factory.DataRoot, "does-not-exist"),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterLibrary_InsideDataRoot_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "Inside Data",
            RootPath = _factory.DataRoot,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterLibrary_DuplicateRoot_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // First registration succeeds
        var first = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "First",
            RootPath = _libRoot,
        });
        first.EnsureSuccessStatusCode();

        // Second with same root fails
        var second = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "Second",
            RootPath = _libRoot,
        });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task GetLibrary_AfterRegistration_ReturnsLibrary()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var regResponse = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "Get Test",
            RootPath = _libRoot,
        });
        regResponse.EnsureSuccessStatusCode();
        var created = await regResponse.Content.ReadFromJsonAsync<LibraryDto>();
        Assert.NotNull(created);
        Assert.False(string.IsNullOrEmpty(created!.Id), $"Library ID was empty. Status: {regResponse.StatusCode}");

        var getResponse = await client.GetAsync($"/api/v1/admin/libraries/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var fetched = await getResponse.Content.ReadFromJsonAsync<LibraryDto>();
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched!.Id);
        Assert.Equal("Get Test", fetched.Name);
    }

    [Fact]
    public async Task UpdateLibrary_ChangesDisplayName()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var regResponse = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "Original Name",
            RootPath = _libRoot,
        });
        var created = await regResponse.Content.ReadFromJsonAsync<LibraryDto>();

        var updateResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/libraries/{created!.Id}/update",
            new UpdateLibraryRequest { DisplayName = "Updated Name" });
        updateResponse.EnsureSuccessStatusCode();

        var updated = await updateResponse.Content.ReadFromJsonAsync<LibraryDto>();
        Assert.Equal("Updated Name", updated!.Name);
    }

    [Fact]
    public async Task DeleteLibrary_Returns204()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var regResponse = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "To Delete",
            RootPath = _libRoot,
        });
        var created = await regResponse.Content.ReadFromJsonAsync<LibraryDto>();

        var delResponse = await client.DeleteAsync($"/api/v1/admin/libraries/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delResponse.StatusCode);

        // Second delete returns 404
        var secondDel = await client.DeleteAsync($"/api/v1/admin/libraries/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, secondDel.StatusCode);
    }

    [Fact]
    public async Task DeleteLibrary_DuringScan_Returns409()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var regResponse = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "To Delete During Scan",
            RootPath = _libRoot,
        });
        var created = await regResponse.Content.ReadFromJsonAsync<LibraryDto>();

        // Seed a running scan (Status == 1) so the delete guard refuses.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<com.lifepixer.mangaplex.Server.Persistence.MangaPlexDbContext>();
            var library = db.Libraries.First(l => l.PublicId == created!.Id);
            db.ScanRuns.Add(new com.lifepixer.mangaplex.Server.Persistence.Entities.ScanRunEntity
            {
                LibraryId = library.Id,
                ScanRevision = 1,
                Status = 1, // running
                StartedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var delResponse = await client.DeleteAsync($"/api/v1/admin/libraries/{created!.Id}");
        Assert.Equal(HttpStatusCode.Conflict, delResponse.StatusCode);
        var error = await delResponse.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("scan_in_progress", error!.Error);

        // The library is still present (the delete was refused).
        var getResponse = await client.GetAsync($"/api/v1/admin/libraries/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task ListUsers_ReturnsAdminUser()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.GetAsync("/api/v1/admin/users");
        response.EnsureSuccessStatusCode();

        var users = await response.Content.ReadFromJsonAsync<List<AdminUserDto>>();
        Assert.NotNull(users);
        Assert.Contains(users!, u => u.Username == "admin");
    }

    [Fact]
    public async Task CreateUser_Returns201()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "newuser",
            Password = "NewUserPass123!",
            IsAdmin = false,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CreateUserResponse>();
        Assert.Equal("newuser", result!.User.Username);
        Assert.False(result.User.IsAdmin);
    }

    [Fact]
    public async Task CreateUser_DuplicateUsername_Returns409()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var first = await client.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "dupuser",
            Password = "FirstPass123!",
            IsAdmin = false,
        });
        first.EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "dupuser",
            Password = "SecondPass123!",
            IsAdmin = false,
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task CreateUser_WeakPassword_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "weakuser",
            Password = "short",
            IsAdmin = false,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateUser_DisableLastAdmin_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Get admin user ID
        var usersResponse = await client.GetAsync("/api/v1/admin/users");
        var users = await usersResponse.Content.ReadFromJsonAsync<List<AdminUserDto>>();
        var admin = users!.First(u => u.Username == "admin");
        Assert.False(string.IsNullOrEmpty(admin.Id), "Admin user ID was empty");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{admin.Id}/update",
            new UpdateUserRequest { IsActive = false });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_ReturnsTempPassword()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Create a user
        var createResponse = await client.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "resettest",
            Password = "OriginalPass123!",
            IsAdmin = false,
        });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var createResult = await createResponse.Content.ReadFromJsonAsync<CreateUserResponse>();
        Assert.NotNull(createResult);
        var createdUser = createResult!.User;

        // Reset password
        var resetResponse = await client.PostAsync(
            $"/api/v1/admin/users/{createdUser.Id}/reset-password", null);
        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);

        var result = await resetResponse.Content.ReadFromJsonAsync<ResetPasswordResponse>();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result!.TemporaryPassword));
    }

    [Fact]
    public async Task GrantAccess_ThenUserCanSeeLibrary()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Register library
        var libResponse = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "Granted Library",
            RootPath = _libRoot,
        });
        var library = await libResponse.Content.ReadFromJsonAsync<LibraryDto>();

        // Create non-admin user
        var userResponse = await client.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "granteduser",
            Password = "GrantedPass123!",
            IsAdmin = false,
        });
        var createdUserResult = await userResponse.Content.ReadFromJsonAsync<CreateUserResponse>();
        var createdUser = createdUserResult!.User;

        // Grant access
        var grantResponse = await client.PutAsync(
            $"/api/v1/admin/users/{createdUser.Id}/grants/{library!.Id}", null);
        Assert.Equal(HttpStatusCode.NoContent, grantResponse.StatusCode);

        // Login as the user and verify they can see the library
        var readerClient = await LoginAndChangePasswordAsync("granteduser", "GrantedPass123!", "GrantedNewPass123!");

        var libsResponse = await readerClient.GetAsync("/api/v1/libraries");
        libsResponse.EnsureSuccessStatusCode();

        var libs = await libsResponse.Content.ReadFromJsonAsync<List<LibraryDto>>();
        Assert.Contains(libs!, l => l.Id == library.Id);
    }

    [Fact]
    public async Task GetUserGrants_ReflectsGrantThenRevoke()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var libResponse = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "Grants List Library",
            RootPath = _libRoot,
        });
        var library = await libResponse.Content.ReadFromJsonAsync<LibraryDto>();

        var userResponse = await client.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "grantslistuser",
            Password = "GrantsListPass123!",
            IsAdmin = false,
        });
        var grantsListResult = await userResponse.Content.ReadFromJsonAsync<CreateUserResponse>();
        var createdUser = grantsListResult!.User;

        // Initially: no grants.
        var before = await client.GetFromJsonAsync<UserGrantsDto>(
            $"/api/v1/admin/users/{createdUser.Id}/grants");
        Assert.False(before!.IsAdmin);
        Assert.Empty(before.LibraryIds);

        // After grant: the library public id is listed.
        await client.PutAsync($"/api/v1/admin/users/{createdUser.Id}/grants/{library!.Id}", null);
        var afterGrant = await client.GetFromJsonAsync<UserGrantsDto>(
            $"/api/v1/admin/users/{createdUser.Id}/grants");
        Assert.Contains(library.Id, afterGrant!.LibraryIds);

        // After revoke: back to empty.
        await client.DeleteAsync($"/api/v1/admin/users/{createdUser.Id}/grants/{library.Id}");
        var afterRevoke = await client.GetFromJsonAsync<UserGrantsDto>(
            $"/api/v1/admin/users/{createdUser.Id}/grants");
        Assert.DoesNotContain(library.Id, afterRevoke!.LibraryIds);
    }

    [Fact]
    public async Task RevokeAccess_ThenUserCannotSeeLibrary()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Setup: library + user + grant
        var libResponse = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "Revoke Library",
            RootPath = _libRoot,
        });
        var library = await libResponse.Content.ReadFromJsonAsync<LibraryDto>();

        var userResponse = await client.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "revokeuser",
            Password = "RevokePass123!",
            IsAdmin = false,
        });
        var revokeResult = await userResponse.Content.ReadFromJsonAsync<CreateUserResponse>();
        var createdUser = revokeResult!.User;

        await client.PutAsync(
            $"/api/v1/admin/users/{createdUser.Id}/grants/{library!.Id}", null);

        // Revoke
        var revokeResponse = await client.DeleteAsync(
            $"/api/v1/admin/users/{createdUser.Id}/grants/{library.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        // Login as user and verify library is not visible
        var readerClient = await LoginAndChangePasswordAsync("revokeuser", "RevokePass123!", "RevokeNewPass123!");

        var libsResponse = await readerClient.GetAsync("/api/v1/libraries");
        libsResponse.EnsureSuccessStatusCode();

        var libs = await libsResponse.Content.ReadFromJsonAsync<List<LibraryDto>>();
        Assert.DoesNotContain(libs!, l => l.Id == library.Id);
    }

    [Fact]
    public async Task TriggerScan_Returns202()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Add some content to the library root so scan has something to do
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series1"));

        var libResponse = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "Scan Library",
            RootPath = _libRoot,
        });
        var library = await libResponse.Content.ReadFromJsonAsync<LibraryDto>();

        var scanResponse = await client.PostAsync($"/api/v1/admin/libraries/{library!.Id}/scan", null);
        Assert.Equal(HttpStatusCode.Accepted, scanResponse.StatusCode);

        var result = await scanResponse.Content.ReadFromJsonAsync<ScanTriggeredDto>();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result!.ScanRunId));
    }

    [Fact]
    public async Task TriggerScan_NonExistentLibrary_Returns404()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var fakeId = com.lifepixer.mangaplex.Core.Catalog.OpaqueId.Encode(99999);
        var response = await client.PostAsync($"/api/v1/admin/libraries/{fakeId}/scan", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetScanHistory_ReturnsList()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        Directory.CreateDirectory(Path.Combine(_libRoot, "Series"));
        var libResponse = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "History Lib",
            RootPath = _libRoot,
        });
        var library = await libResponse.Content.ReadFromJsonAsync<LibraryDto>();

        // Trigger a scan
        await client.PostAsync($"/api/v1/admin/libraries/{library!.Id}/scan", null);

        // Give the background task a moment to complete
        await Task.Delay(500);

        var historyResponse = await client.GetAsync($"/api/v1/admin/libraries/{library.Id}/scans");
        historyResponse.EnsureSuccessStatusCode();

        var scans = await historyResponse.Content.ReadFromJsonAsync<List<ScanRunDto>>();
        Assert.NotNull(scans);
    }

    // --- Activation Token Tests ---

    [Fact]
    public async Task CreateUser_WithoutPassword_ReturnsPendingUserAndActivationUrl()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "activateuser1",
            IsAdmin = false,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CreateUserResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result!.ActivationUrl);
        Assert.Contains("/activate?token=", result.ActivationUrl);
        Assert.True(result.User.IsPendingActivation);
        Assert.Equal("activateuser1", result.User.Username);
    }

    [Fact]
    public async Task PendingUser_CannotLogin()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        await client.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "pendinglogin",
            IsAdmin = false,
        });

        var anonClient = _factory.CreateClient();
        var loginResponse = await anonClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "pendinglogin",
            Password = "anything12345",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, loginResponse.StatusCode);
        var error = await loginResponse.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("invalid_credentials", error!.Error);
    }

    [Fact]
    public async Task ActivateAccount_ValidToken_ActivatesAndSignsIn()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var createResponse = await client.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "activatevalid",
            IsAdmin = false,
        });
        var createResult = await createResponse.Content.ReadFromJsonAsync<CreateUserResponse>();
        var token = ExtractToken(createResult!.ActivationUrl!);

        var anonClient = _factory.CreateClient();
        var activateResponse = await anonClient.PostAsJsonAsync("/api/v1/auth/activate", new ActivateAccountRequest
        {
            Token = token,
            Password = "MyNewPassword123!",
        });

        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);
        var user = await activateResponse.Content.ReadFromJsonAsync<AuthUserDto>();
        Assert.Equal("activatevalid", user!.Username);
        Assert.False(user.ForcePasswordChange);

        // Verify the user can now log in with the chosen password
        var loginClient = _factory.CreateClient();
        var loginResponse = await loginClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "activatevalid",
            Password = "MyNewPassword123!",
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    [Fact]
    public async Task ActivateAccount_ReusedToken_Fails()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var createResponse = await client.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "activatereuse",
            IsAdmin = false,
        });
        var createResult = await createResponse.Content.ReadFromJsonAsync<CreateUserResponse>();
        var token = ExtractToken(createResult!.ActivationUrl!);

        var anonClient = _factory.CreateClient();
        var first = await anonClient.PostAsJsonAsync("/api/v1/auth/activate", new ActivateAccountRequest
        {
            Token = token,
            Password = "FirstPassword123!",
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/activate", new ActivateAccountRequest
        {
            Token = token,
            Password = "SecondPassword123!",
        });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var error = await second.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("invalid_token", error!.Error);
    }

    [Fact]
    public async Task ActivateAccount_InvalidToken_DoesNotLeakUserExistence()
    {
        var anonClient = _factory.CreateClient();
        var response = await anonClient.PostAsJsonAsync("/api/v1/auth/activate", new ActivateAccountRequest
        {
            Token = "this-is-not-a-real-token-at-all",
            Password = "SomePassword123!",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("invalid_token", error!.Error);
        Assert.Equal("Invalid or expired activation link.", error.Message);
    }

    [Fact]
    public async Task CreateUser_WithPassword_StillWorks()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "passworduser",
            Password = "StrongPass123!",
            IsAdmin = false,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CreateUserResponse>();
        Assert.NotNull(result);
        Assert.Null(result!.ActivationUrl);
        Assert.False(result.User.IsPendingActivation);
    }

    [Fact]
    public async Task FirstRunSetup_StillWorks_WithActivationFeature()
    {
        using var factory = new MangaPlexWebApplicationFactory();
        var client = factory.CreateClient();

        var setupStatus = await client.GetFromJsonAsync<SetupStatusDto>("/api/v1/auth/setup-status");
        Assert.True(setupStatus!.SetupRequired);

        var setupResponse = await client.PostAsJsonAsync("/api/v1/auth/setup", new SetupRequest
        {
            Username = "firstadmin",
            Password = "FirstAdmin123!",
        });
        Assert.Equal(HttpStatusCode.OK, setupResponse.StatusCode);

        var status2 = await client.GetFromJsonAsync<SetupStatusDto>("/api/v1/auth/setup-status");
        Assert.False(status2!.SetupRequired);
    }

    private static string ExtractToken(string activationUrl)
    {
        var uri = new Uri(activationUrl);
        var pairs = uri.Query.TrimStart('?').Split('&');
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "token")
                return Uri.UnescapeDataString(parts[1]);
        }
        throw new InvalidOperationException("No token query parameter in activation URL");
    }
}
