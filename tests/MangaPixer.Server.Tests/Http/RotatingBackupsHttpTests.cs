namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using Xunit;

/// <summary>
/// HTTP integration tests for the rotating DB backups feature, exercised
/// through the public operations API:
/// - GET /api/v1/operations/backups reports configuration and last-run status.
/// - POST /api/v1/operations/backups/rotating runs a backup now and the
///   snapshot file appears under the private data root's backups folder.
/// - Non-admin users get 403 on both endpoints.
/// </summary>
[Collection("HttpSerial")]
public sealed class RotatingBackupsHttpTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory;

    public RotatingBackupsHttpTests()
    {
        _factory = new MangaPixerWebApplicationFactory();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task GetBackups_ReturnsStatus()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.GetAsync("/api/v1/operations/backups");

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dto.GetProperty("enabled").GetBoolean());
        Assert.Equal(24, dto.GetProperty("intervalHours").GetDouble());
        Assert.Equal(7, dto.GetProperty("retentionCount").GetInt32());
        Assert.Equal(0, dto.GetProperty("retainedCount").GetInt32());
    }

    [Fact]
    public async Task PostRotatingBackup_CreatesSnapshot_AndUpdatesStatus()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PostAsync("/api/v1/operations/backups/rotating", content: null);

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        var fileName = dto.GetProperty("lastBackupFileName").GetString();
        Assert.False(string.IsNullOrWhiteSpace(fileName));
        Assert.StartsWith("rotating-", fileName);
        Assert.NotEqual(JsonValueKind.Null, dto.GetProperty("lastSuccessUtc").ValueKind);

        // The snapshot exists in the private data root; the API exposed only
        // the generated file name, never an absolute path.
        var backupFile = Path.Combine(_factory.DataRoot, "backups", fileName!);
        Assert.True(File.Exists(backupFile), $"Expected snapshot file {fileName} under the data root backups folder.");
        Assert.Equal(1, dto.GetProperty("retainedCount").GetInt32());
    }

    [Fact]
    public async Task GetBackups_NonAdmin_Returns403()
    {
        var adminClient = await _factory.LoginAsAdminWithChangedPasswordAsync();

        await adminClient.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "reader",
            Password = "ReaderPass123!",
            IsAdmin = false,
        });

        var readerClient = await LoginReaderAsync("reader");

        var response = await readerClient.GetAsync("/api/v1/operations/backups");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostRotatingBackup_NonAdmin_Returns403()
    {
        var adminClient = await _factory.LoginAsAdminWithChangedPasswordAsync();

        await adminClient.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "reader2",
            Password = "ReaderPass123!",
            IsAdmin = false,
        });

        var readerClient = await LoginReaderAsync("reader2");

        var response = await readerClient.PostAsync("/api/v1/operations/backups/rotating", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Creates the non-admin user (assumed already registered by the admin),
    /// logs in, clears the forced password change, and re-logs in — mirroring
    /// the LogLevelHttpTests non-admin flow.
    /// </summary>
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
