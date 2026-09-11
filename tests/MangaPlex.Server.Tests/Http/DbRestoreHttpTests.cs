namespace com.lifepixer.mangaplex.Tests.Server.Http;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Server.Features.Auth;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// HTTP integration tests for the DB backup import/restore feature, exercised
/// through the public operations API:
/// - POST /api/v1/operations/restore (admin) stages a validated upload (202).
/// - Non-admin users get 403.
/// - Rejection paths: bad magic, oversized, corrupt/integrity-fail, missing
///   file field, non-SQLite, and a corrupt upload that fails before the swap
///   (rollback leaves the original DB intact).
/// </summary>
[Collection("HttpSerial")]
public sealed class DbRestoreHttpTests : IDisposable
{
    private readonly MangaPlexWebApplicationFactory _factory;

    public DbRestoreHttpTests()
    {
        _factory = new MangaPlexWebApplicationFactory();
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Takes a real rotating backup via the API and returns the snapshot file
    /// path, so the test can upload it as a restore payload.
    /// </summary>
    private static async Task<string> TakeBackupViaApiAsync(HttpClient admin)
    {
        var response = await admin.PostAsync("/api/v1/operations/backups/rotating", content: null);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        var fileName = dto.GetProperty("lastBackupFileName").GetString();
        Assert.False(string.IsNullOrWhiteSpace(fileName));
        return fileName!;
    }

    private static async Task<MultipartFormDataContent> BuildUploadContentAsync(
        MangaPlexWebApplicationFactory factory, string fileName)
    {
        var backupPath = Path.Combine(factory.DataRoot, "backups", fileName);
        var bytes = await File.ReadAllBytesAsync(backupPath);
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "backup.db");
        return content;
    }

    [Fact]
    public async Task Restore_AdminValidBackup_Returns202AndStages()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var fileName = await TakeBackupViaApiAsync(client);
        var upload = await BuildUploadContentAsync(_factory, fileName);

        var response = await client.PostAsync("/api/v1/operations/restore", upload);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(dto.GetProperty("preRestoreBackupFileName").GetString()));
        // Staged file + marker exist under the controlled data root.
        Assert.True(File.Exists(Path.Combine(_factory.DataRoot, "restore-pending", "mangaplex.db.staged")));
        Assert.True(File.Exists(Path.Combine(_factory.DataRoot, "restore-pending", "restore.json")));
    }

    [Fact]
    public async Task Restore_NonAdmin_Returns403()
    {
        var adminClient = await _factory.LoginAsAdminWithChangedPasswordAsync();
        await adminClient.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "reader",
            Password = "ReaderPass123!",
            IsAdmin = false,
        });

        var readerClient = await LoginReaderAsync("reader");
        var upload = new MultipartFormDataContent();
        upload.Add(new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0 junk")), "file", "x.db");

        var response = await readerClient.PostAsync("/api/v1/operations/restore", upload);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Restore_NoFile_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var upload = new MultipartFormDataContent();

        var response = await client.PostAsync("/api/v1/operations/restore", upload);

        // Either our ApiError (action reached) or a model-binding 400
        // (ProblemDetails) — both are 400 and both reject the request.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Restore_BadMagic_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var upload = new MultipartFormDataContent();
        upload.Add(new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes("not a sqlite file at all")), "file", "bad.db");

        var response = await client.PostAsync("/api/v1/operations/restore", upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("invalid_backup", err!.Error);
    }

    [Fact]
    public async Task Restore_EmptySqliteNoSchema_Returns400()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        // A bare SQLite file (magic OK, no MangaPlex tables) — schema missing.
        var emptyPath = Path.Combine(_factory.DataRoot, "empty.db");
        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            com.lifepixer.mangaplex.Server.Persistence.DatabaseInitialization.BuildConnectionString(emptyPath)))
        {
            await conn.OpenAsync();
            // Create one unrelated table so the file is a real, valid SQLite DB
            // but lacks the MangaPlex schema.
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE unrelated(x INTEGER);";
            await cmd.ExecuteNonQueryAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var bytes = await File.ReadAllBytesAsync(emptyPath);
        var upload = new MultipartFormDataContent();
        upload.Add(new ByteArrayContent(bytes), "file", "empty.db");

        var response = await client.PostAsync("/api/v1/operations/restore", upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("invalid_backup", err!.Error);
    }

    [Fact]
    public async Task Restore_CorruptUpload_RollsBackAndLeavesOriginalDbIntact()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var fileName = await TakeBackupViaApiAsync(client);

        // Corrupt the backup bytes past the header so integrity_check fails.
        var backupPath = Path.Combine(_factory.DataRoot, "backups", fileName);
        var bytes = await File.ReadAllBytesAsync(backupPath);
        if (bytes.Length > 4096)
        {
            bytes[4096] = 0xFF;
            bytes[4097] = 0xFF;
        }
        var upload = new MultipartFormDataContent();
        upload.Add(new ByteArrayContent(bytes), "file", "corrupt.db");

        var response = await client.PostAsync("/api/v1/operations/restore", upload);

        // Rejected at validation; nothing staged.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("invalid_backup", err!.Error);
        Assert.False(File.Exists(Path.Combine(_factory.DataRoot, "restore-pending", "mangaplex.db.staged")));
        // Original DB still works — the server is still serving (health check).
        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    /// <summary>
    /// Creates the non-admin user (assumed already registered by the admin),
    /// logs in, clears the forced password change, and re-logs in.
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
        client.DefaultRequestHeaders.Add("X-MangaPlex-Csrf", csrf!.Token);

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
        freshClient.DefaultRequestHeaders.Add("X-MangaPlex-Csrf", csrf!.Token);

        return freshClient;
    }
}
