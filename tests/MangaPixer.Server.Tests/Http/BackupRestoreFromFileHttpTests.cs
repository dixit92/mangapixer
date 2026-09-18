namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using Xunit;

/// <summary>
/// HTTP integration tests (1.18.0) for the DB backup RESTORE-from-snapshot
/// feature, exercised through the public operations API:
/// - GET /api/v1/operations/backups/files lists on-disk rotating snapshots
///   (file name + size + timestamp, never a path).
/// - POST /api/v1/operations/backups/restore stages a restore from a chosen
///   snapshot file name (202) and rejects any path/traversal or missing file.
/// - Non-admin users get 403 on both.
/// </summary>
[Collection("HttpSerial")]
public sealed class BackupRestoreFromFileHttpTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory;

    public BackupRestoreFromFileHttpTests()
    {
        _factory = new MangaPixerWebApplicationFactory();
    }

    public void Dispose() => _factory.Dispose();

    private static async Task<string> TakeBackupViaApiAsync(HttpClient admin)
    {
        var response = await admin.PostAsync("/api/v1/operations/backups/rotating", content: null);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        var fileName = dto.GetProperty("lastBackupFileName").GetString();
        Assert.False(string.IsNullOrWhiteSpace(fileName));
        return fileName!;
    }

    [Fact]
    public async Task ListBackupFiles_AfterBackup_ReturnsSnapshot_NoPaths()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var fileName = await TakeBackupViaApiAsync(client);

        var response = await client.GetAsync("/api/v1/operations/backups/files");

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        var files = dto.GetProperty("files");
        Assert.Equal(JsonValueKind.Array, files.ValueKind);
        Assert.True(files.GetArrayLength() >= 1);

        var first = files[0];
        Assert.Equal(fileName, first.GetProperty("fileName").GetString());
        Assert.True(first.GetProperty("byteSize").GetInt64() > 0);
        Assert.NotEqual(JsonValueKind.Null, first.GetProperty("timestampUtc").ValueKind);

        // Privacy invariant: no absolute path leaks in the serialized listing.
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_factory.DataRoot, raw);
    }

    [Fact]
    public async Task RestoreFromBackup_ValidSnapshot_Returns202AndStages()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var fileName = await TakeBackupViaApiAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/operations/backups/restore",
            new { fileName });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(dto.GetProperty("preRestoreBackupFileName").GetString()));
        Assert.True(File.Exists(Path.Combine(_factory.DataRoot, "restore-pending", "mangapixer.db.staged")));
        Assert.True(File.Exists(Path.Combine(_factory.DataRoot, "restore-pending", "restore.json")));
    }

    [Fact]
    public async Task RestoreFromBackup_PathTraversal_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PostAsJsonAsync("/api/v1/operations/backups/restore",
            new { fileName = "../mangapixer.db" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("invalid_backup_name", err!.Error);
        // Nothing was staged.
        Assert.False(File.Exists(Path.Combine(_factory.DataRoot, "restore-pending", "mangapixer.db.staged")));
    }

    [Fact]
    public async Task RestoreFromBackup_MissingFile_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PostAsJsonAsync("/api/v1/operations/backups/restore",
            new { fileName = "rotating-19990101-000000.db" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("backup_not_found", err!.Error);
    }

    [Fact]
    public async Task RestoreFromBackup_EmptyName_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PostAsJsonAsync("/api/v1/operations/backups/restore",
            new { fileName = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListBackupFiles_NonAdmin_Returns403()
    {
        var adminClient = await _factory.LoginAsAdminWithChangedPasswordAsync();
        await adminClient.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "reader",
            Password = "ReaderPass123!",
            IsAdmin = false,
        });
        var readerClient = await LoginReaderAsync("reader");

        var response = await readerClient.GetAsync("/api/v1/operations/backups/files");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RestoreFromBackup_NonAdmin_Returns403()
    {
        var adminClient = await _factory.LoginAsAdminWithChangedPasswordAsync();
        await adminClient.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "reader2",
            Password = "ReaderPass123!",
            IsAdmin = false,
        });
        var readerClient = await LoginReaderAsync("reader2");

        var response = await readerClient.PostAsJsonAsync("/api/v1/operations/backups/restore",
            new { fileName = "rotating-x.db" });

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
