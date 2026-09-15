namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP integration tests for the YACReader progress import endpoints
/// (admin-only). Exercises the full wiring — DI, routing, auth, CSRF, and
/// serialization — through WebApplicationFactory, with a synthetic YACReader
/// library.ydb fixture and a directly-seeded MangaPixer catalog.
/// </summary>
[Collection("HttpSerial")]
public sealed class YacReaderImportHttpTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory;
    private readonly string _libRoot;

    public YacReaderImportHttpTests()
    {
        _factory = new MangaPixerWebApplicationFactory();
        _libRoot = Path.Combine(Path.GetTempPath(), "mangapixer-yac-http-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_libRoot);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_libRoot, true); } catch { }
    }

    private static void BuildYacLibrary(string parentDir, params (long id, string path, string fileName, int currentPage, bool read, bool hasBeenOpened)[] comics)
    {
        var libDir = Path.Combine(parentDir, ".yacreaderlibrary");
        Directory.CreateDirectory(libDir);
        var dbFile = Path.Combine(libDir, "library.ydb");

        var builder = new SqliteConnectionStringBuilder { DataSource = dbFile, Mode = SqliteOpenMode.ReadWriteCreate };
        using var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE db_info (version TEXT NOT NULL);
                CREATE TABLE comic_info (id INTEGER PRIMARY KEY, currentPage INTEGER DEFAULT 1, read BOOLEAN DEFAULT 0, hasBeenOpened BOOLEAN DEFAULT 0, lastTimeOpened INTEGER);
                CREATE TABLE comic (id INTEGER PRIMARY KEY, parentId INTEGER NOT NULL, comicInfoId INTEGER NOT NULL, fileName TEXT NOT NULL, path TEXT);
                INSERT INTO db_info (version) VALUES ('9.0.0');
                """;
            cmd.ExecuteNonQuery();
        }

        foreach (var c in comics)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO comic_info (id, currentPage, read, hasBeenOpened, lastTimeOpened) VALUES ($id, $page, $read, $opened, 0);
                INSERT INTO comic (id, parentId, comicInfoId, fileName, path) VALUES ($id, 1, $id, $fileName, $path);
                """;
            cmd.Parameters.AddWithValue("$id", c.id);
            cmd.Parameters.AddWithValue("$page", c.currentPage);
            cmd.Parameters.AddWithValue("$read", c.read ? 1 : 0);
            cmd.Parameters.AddWithValue("$opened", c.hasBeenOpened ? 1 : 0);
            cmd.Parameters.AddWithValue("$fileName", c.fileName);
            cmd.Parameters.AddWithValue("$path", (object?)c.path ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private async Task<(string libraryPublicId, string userPublicId, long node1Id)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();

        var library = new LibraryEntity
        {
            PublicId = "yaclib1",
            DisplayName = "YAC Test",
            RootPath = "/tmp/yac-test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        var node1 = new CatalogNodeEntity
        {
            PublicId = "yacnode1",
            LibraryId = library.Id,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = "Volume 1.cbz",
            RelativePath = "/Series/Volume 1.cbz",
            PathKey = "/Series/Volume 1.cbz",
            SortKey = "1Volume 1.cbz",
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node1);
        await db.SaveChangesAsync();

        db.ArchiveItems.Add(new ArchiveItemEntity
        {
            NodeId = node1.Id,
            ArchiveFormat = 0,
            ByteLength = 1024,
            ModificationTicks = 0,
            ContentVersion = 1,
            AnalysisState = 0,
            PageCount = 10,
        });
        await db.SaveChangesAsync();

        // The admin created by LoginAsAdminWithChangedPasswordAsync has a
        // known public id we can resolve from the DB.
        var admin = await db.Users.FirstAsync(u => u.IsAdmin);
        return (library.PublicId, admin.PublicId, node1.Id);
    }

    [Fact]
    public async Task Preview_NonAdmin_Returns403()
    {
        var adminClient = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Create a non-admin user.
        var createResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "reader1",
            Password = "ReaderPass123!",
            IsAdmin = false,
        });
        createResponse.EnsureSuccessStatusCode();

        var readerClient = _factory.CreateClient();
        var loginResponse = await readerClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "reader1",
            Password = "ReaderPass123!",
        });
        loginResponse.EnsureSuccessStatusCode();

        var csrfResponse = await readerClient.GetAsync("/api/v1/auth/csrf");
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        readerClient.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);

        // Change password to clear ForcePasswordChange.
        await readerClient.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "ReaderPass123!",
            NewPassword = "ReaderNewPass123!",
        });
        readerClient = _factory.CreateClient();
        await readerClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "reader1", Password = "ReaderNewPass123!" });
        var freshCsrf = await readerClient.GetAsync("/api/v1/auth/csrf");
        var freshToken = await freshCsrf.Content.ReadFromJsonAsync<CsrfTokenDto>();
        readerClient.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", freshToken!.Token);

        var response = await readerClient.PostAsJsonAsync("/api/v1/admin/import/yacreader/preview", new YacReaderImportRequest
        {
            LibraryId = "yaclib1",
            YacDbPath = _libRoot,
            TargetUserId = "x",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Preview_Admin_ReturnsMappingAndDbVersion()
    {
        var adminClient = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var (libraryPublicId, userPublicId, node1Id) = await SeedAsync();

        BuildYacLibrary(_libRoot,
            (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 5, read: false, hasBeenOpened: true),
            (2, "/Series/Volume 2.cbz", "Volume 2.cbz", 1, read: false, hasBeenOpened: false));

        var response = await adminClient.PostAsJsonAsync("/api/v1/admin/import/yacreader/preview", new YacReaderImportRequest
        {
            LibraryId = libraryPublicId,
            YacDbPath = _libRoot,
            TargetUserId = userPublicId,
        });
        response.EnsureSuccessStatusCode();

        var preview = await response.Content.ReadFromJsonAsync<YacReaderImportPreviewDto>();
        Assert.NotNull(preview);
        Assert.Equal("9.0.0", preview!.DbVersion);
        Assert.Equal(2, preview.TotalComics);
        Assert.Equal(1, preview.Mapped);
        Assert.Equal(1, preview.Unmapped);
        Assert.Equal(1, preview.ToImport);
        Assert.Single(preview.Items);
        Assert.Equal("inProgress", preview.Items[0].State);
    }

    [Fact]
    public async Task Apply_Admin_WritesProgressAndReadMark()
    {
        var adminClient = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var (libraryPublicId, userPublicId, node1Id) = await SeedAsync();

        BuildYacLibrary(_libRoot,
            (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 10, read: true, hasBeenOpened: true));

        var applyResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/import/yacreader/apply", new YacReaderImportRequest
        {
            LibraryId = libraryPublicId,
            YacDbPath = _libRoot,
            TargetUserId = userPublicId,
        });
        applyResponse.EnsureSuccessStatusCode();

        var result = await applyResponse.Content.ReadFromJsonAsync<YacReaderImportResultDto>();
        Assert.NotNull(result);
        Assert.Equal(1, result!.Imported);
        Assert.Equal(1, result.ReadMarks);

        // Verify via the reading progress API that the admin can now see the imported progress.
        var progressResponse = await adminClient.GetAsync($"/api/v1/reading/progress/yacnode1");
        progressResponse.EnsureSuccessStatusCode();
        var progress = await progressResponse.Content.ReadFromJsonAsync<ReadingProgressDto>(TestJson.Web);
        Assert.NotNull(progress);
        Assert.Equal(9, progress!.PageIndex); // currentPage 10 → 0-based 9
        Assert.Equal(Core.Reading.ReadingState.Completed, progress.State);

        var readMarkResponse = await adminClient.GetAsync("/api/v1/reading/yacnode1/read");
        readMarkResponse.EnsureSuccessStatusCode();
        var readMark = await readMarkResponse.Content.ReadFromJsonAsync<ReadMarkDto>();
        Assert.NotNull(readMark);
        Assert.True(readMark!.IsRead);
    }

    [Fact]
    public async Task Preview_Admin_MissingDatabase_ReturnsBadRequest()
    {
        var adminClient = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var (libraryPublicId, userPublicId, _) = await SeedAsync();

        var response = await adminClient.PostAsJsonAsync("/api/v1/admin/import/yacreader/preview", new YacReaderImportRequest
        {
            LibraryId = libraryPublicId,
            YacDbPath = Path.Combine(_libRoot, "no-such-dir"),
            TargetUserId = userPublicId,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
