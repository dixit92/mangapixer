namespace com.lifepixer.mangapixer.Tests.Server.Features.Import;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Import.YacReader;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-level integration tests for <see cref="YacReaderImportService"/>.
/// Builds a synthetic YACReader library.ydb fixture and a MangaPixer catalog,
/// then verifies preview (dry-run) and apply (write) behavior, the conflict
/// policy, path mapping, and the read-only/snapshot source invariant.
/// </summary>
public sealed class YacReaderImportServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _scratchRoot;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public YacReaderImportServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-yac-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "mangapixer.db");
        _scratchRoot = Path.Combine(_tempDir, "scratch");
        Directory.CreateDirectory(_scratchRoot);
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext db, LibraryEntity library, UserEntity user, CatalogNodeEntity node1, CatalogNodeEntity node2)> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var library = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Test",
            RootPath = "/private/test",
            CaseComparisonPolicy = "ordinal",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);

        var user = new UserEntity
        {
            PublicId = OpaqueId.Encode(2),
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var node1 = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(100),
            LibraryId = library.Id,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = "Volume 1.cbz",
            RelativePath = "/Series/Volume 1.cbz",
            PathKey = "/Series/Volume 1.cbz",
            SortKey = "1Volume 1.cbz",
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var node2 = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(101),
            LibraryId = library.Id,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = "Volume 2.cbz",
            RelativePath = "/Series/Volume 2.cbz",
            PathKey = "/Series/Volume 2.cbz",
            SortKey = "1Volume 2.cbz",
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.AddRange(node1, node2);
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
        db.ArchiveItems.Add(new ArchiveItemEntity
        {
            NodeId = node2.Id,
            ArchiveFormat = 0,
            ByteLength = 1024,
            ModificationTicks = 0,
            ContentVersion = 1,
            AnalysisState = 0,
            PageCount = 20,
        });

        await db.SaveChangesAsync();
        return (db, library, user, node1, node2);
    }

    /// <summary>
    /// Builds a synthetic YACReader library.ydb with the verified schema and the
    /// given comic rows. Returns the path to the .yacreaderlibrary directory.
    /// </summary>
    private static string BuildYacLibrary(string parentDir, params (long id, string path, string fileName, int currentPage, bool read, bool hasBeenOpened)[] comics)
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

        return libDir;
    }

    private YacReaderImportService CreateService(MangaPixerDbContext db)
        => new(db, new YacReaderLibraryReader(), new AppRootOptions { ScratchRoot = _scratchRoot },
            NullLogger<YacReaderImportService>.Instance);

    private YacReaderImportRequest Request(string yacDir, string libraryPublicId, string userPublicId, bool overwrite = false, bool snapshot = true)
        => new()
        {
            LibraryId = libraryPublicId,
            YacDbPath = yacDir,
            TargetUserId = userPublicId,
            Overwrite = overwrite,
            Snapshot = snapshot,
        };

    [Fact]
    public async Task Preview_DryRun_WritesNothingAndMapsByPath()
    {
        var setup = await SetupAsync();
        var (db, library, user, node1, node2) = setup;

        // comic.path holds the folder-relative path including the file name;
        // comic.fileName holds the file name. The importer tries path+fileName,
        // then path alone, then fileName alone.
        var yacDir = BuildYacLibrary(_tempDir,
            (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 5, read: false, hasBeenOpened: true),
            (2, "/Series/Volume 2.cbz", "Volume 2.cbz", 20, read: true, hasBeenOpened: true),
            (3, "/Series/Volume 3.cbz", "Volume 3.cbz", 1, read: false, hasBeenOpened: false));

        var service = CreateService(db);
        var result = await service.PreviewAsync(Request(yacDir, library.PublicId, user.PublicId), default);

        Assert.True(result.Success, result.Message);
        Assert.Equal("9.0.0", result.Value!.DbVersion);
        Assert.Equal(3, result.Value.TotalComics);
        Assert.Equal(2, result.Value.Mapped);   // Volume 3 has no matching node
        Assert.Equal(1, result.Value.Unmapped);
        Assert.Equal(0, result.Value.Conflicts);
        Assert.Equal(2, result.Value.ToImport); // both mapped comics are non-unread

        // Dry-run writes no progress.
        Assert.Equal(0, await db.ReadingProgress.CountAsync());
        Assert.Equal(0, await db.ReadMarks.CountAsync());
    }

    [Fact]
    public async Task Detect_FindsYacLibraryUnderLibraryRoot_AndReportsAbsentWhenMissing()
    {
        var (db, library, user, _, _) = await SetupAsync();
        var svc = CreateService(db);

        // Library root has no .yacreaderlibrary yet → not detected.
        var before = await svc.DetectAsync(library.PublicId, default);
        Assert.True(before.Success);
        Assert.False(before.Value!.Detected);

        // Point the library at a real temp root that contains a YACReader db.
        library.RootPath = _tempDir;
        await db.SaveChangesAsync();
        BuildYacLibrary(_tempDir, (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 5, read: false, hasBeenOpened: true));

        var after = await svc.DetectAsync(library.PublicId, default);
        Assert.True(after.Success);
        Assert.True(after.Value!.Detected);
        Assert.Equal("9.0.0", after.Value.DbVersion);
    }

    [Fact]
    public async Task Preview_AutoResolvesDbFromLibraryRoot_WhenNoPathGiven()
    {
        var (db, library, user, _, _) = await SetupAsync();
        library.RootPath = _tempDir;
        await db.SaveChangesAsync();
        BuildYacLibrary(_tempDir,
            (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 5, read: false, hasBeenOpened: true),
            (2, "/Series/Volume 2.cbz", "Volume 2.cbz", 20, read: true, hasBeenOpened: true));

        var svc = CreateService(db);
        // No YacDbPath — the server must resolve .yacreaderlibrary/library.ydb from RootPath.
        var result = await svc.PreviewAsync(new YacReaderImportRequest
        {
            LibraryId = library.PublicId,
            TargetUserId = user.PublicId,
        }, default);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.Value!.Mapped);
        Assert.Equal(2, result.Value.ToImport);
    }

    [Fact]
    public async Task Apply_WritesProgressAndReadMarksForMappedComics()
    {
        var setup = await SetupAsync();
        var (db, library, user, node1, node2) = setup;

        var yacDir = BuildYacLibrary(_tempDir,
            (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 5, read: false, hasBeenOpened: true),
            (2, "/Series/Volume 2.cbz", "Volume 2.cbz", 20, read: true, hasBeenOpened: true),
            (3, "/Series/Volume 3.cbz", "Volume 3.cbz", 1, read: false, hasBeenOpened: false));

        var service = CreateService(db);
        var result = await service.ApplyAsync(Request(yacDir, library.PublicId, user.PublicId), default);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.Value!.Imported);
        Assert.Equal(1, result.Value.ReadMarks); // only Volume 2 is read
        Assert.Equal(0, result.Value.Skipped);

        var progress1 = await db.ReadingProgress.FirstOrDefaultAsync(p => p.ItemId == node1.Id);
        Assert.NotNull(progress1);
        Assert.Equal(4, progress1!.Ordinal); // currentPage 5 → 0-based 4
        Assert.Equal((int)Core.Reading.ReadingState.InProgress, progress1.State);

        var progress2 = await db.ReadingProgress.FirstOrDefaultAsync(p => p.ItemId == node2.Id);
        Assert.NotNull(progress2);
        Assert.Equal(19, progress2!.Ordinal); // currentPage 20 → 0-based 19
        Assert.Equal((int)Core.Reading.ReadingState.Completed, progress2.State);

        var mark2 = await db.ReadMarks.FirstOrDefaultAsync(m => m.ItemId == node2.Id);
        Assert.NotNull(mark2);
        Assert.Equal("import", mark2!.Source);

        // Unread comic (Volume 3) is unmapped anyway; no progress for it.
        Assert.Null(await db.ReadingProgress.FirstOrDefaultAsync(p => p.EntryKey == OpaqueId.Encode(0) && p.Ordinal == 0 && p.ItemId != node1.Id && p.ItemId != node2.Id));
    }

    [Fact]
    public async Task Apply_SkipsConflictsUnlessOverwrite()
    {
        var setup = await SetupAsync();
        var (db, library, user, node1, node2) = setup;

        // Pre-existing MangaPixer progress for node1 (a conflict).
        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = user.Id,
            ItemId = node1.Id,
            ContentVersion = 1,
            EntryKey = OpaqueId.Encode(0),
            Ordinal = 0,
            State = (int)Core.Reading.ReadingState.InProgress,
            Revision = 1,
            LastMutationId = "existing",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var yacDir = BuildYacLibrary(_tempDir,
            (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 7, read: false, hasBeenOpened: true),
            (2, "/Series/Volume 2.cbz", "Volume 2.cbz", 20, read: true, hasBeenOpened: true));

        var service = CreateService(db);

        // overwrite=false → conflict skipped, existing progress preserved.
        var result = await service.ApplyAsync(Request(yacDir, library.PublicId, user.PublicId, overwrite: false), default);
        Assert.True(result.Success, $"{result.Error}: {result.Message}");
        Assert.Equal(1, result.Value!.Imported); // only Volume 2
        Assert.Equal(1, result.Value.Skipped);

        var progress1 = await db.ReadingProgress.FirstAsync(p => p.ItemId == node1.Id);
        Assert.Equal(0, progress1.Ordinal); // unchanged
        Assert.Equal("existing", progress1.LastMutationId);

        // overwrite=true → conflict overwritten.
        var result2 = await service.ApplyAsync(Request(yacDir, library.PublicId, user.PublicId, overwrite: true), default);
        Assert.True(result2.Success);
        Assert.Equal(2, result2.Value!.Imported);
        Assert.Equal(0, result2.Value.Skipped);

        var progress1b = await db.ReadingProgress.FirstAsync(p => p.ItemId == node1.Id);
        Assert.Equal(6, progress1b.Ordinal); // currentPage 7 → 0-based 6
        Assert.StartsWith("yacreader-import:", progress1b.LastMutationId);
    }

    [Fact]
    public async Task Preview_DetectsConflicts()
    {
        var setup = await SetupAsync();
        var (db, library, user, node1, node2) = setup;

        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = user.Id,
            ItemId = node1.Id,
            ContentVersion = 1,
            EntryKey = OpaqueId.Encode(0),
            Ordinal = 0,
            State = (int)Core.Reading.ReadingState.InProgress,
            Revision = 1,
            LastMutationId = "existing",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var yacDir = BuildYacLibrary(_tempDir,
            (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 7, read: false, hasBeenOpened: true),
            (2, "/Series/Volume 2.cbz", "Volume 2.cbz", 20, read: true, hasBeenOpened: true));

        var service = CreateService(db);
        var result = await service.PreviewAsync(Request(yacDir, library.PublicId, user.PublicId), default);

        Assert.True(result.Success);
        Assert.Equal(1, result.Value!.Conflicts);
        Assert.Equal(1, result.Value.ToImport); // Volume 2 only (conflict excluded without overwrite)
        Assert.Contains(result.Value.Items, i => i.Conflict);
    }

    [Fact]
    public async Task Snapshot_DoesNotWriteToSourceDirectory()
    {
        var setup = await SetupAsync();
        var (db, library, user, node1, node2) = setup;

        var yacDir = BuildYacLibrary(_tempDir,
            (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 5, read: false, hasBeenOpened: true));

        var filesBefore = Directory.GetFiles(yacDir, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList();

        var service = CreateService(db);
        var result = await service.PreviewAsync(Request(yacDir, library.PublicId, user.PublicId, snapshot: true), default);

        Assert.True(result.Success);

        var filesAfter = Directory.GetFiles(yacDir, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList();
        Assert.Equal(filesBefore, filesAfter); // no journal/wal/shm sidecars created in the source dir
    }

    [Fact]
    public async Task Apply_NoSnapshot_OpensSourceReadOnlyWithoutWriting()
    {
        var setup = await SetupAsync();
        var (db, library, user, node1, node2) = setup;

        var yacDir = BuildYacLibrary(_tempDir,
            (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 5, read: false, hasBeenOpened: true));

        var filesBefore = Directory.GetFiles(yacDir, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList();

        var service = CreateService(db);
        var result = await service.ApplyAsync(Request(yacDir, library.PublicId, user.PublicId, snapshot: false), default);

        Assert.True(result.Success);
        var filesAfter = Directory.GetFiles(yacDir, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList();
        Assert.Equal(filesBefore, filesAfter);
    }

    [Fact]
    public async Task Preview_ReturnsErrorForMissingDatabase()
    {
        var setup = await SetupAsync();
        var (db, library, user, node1, node2) = setup;

        var service = CreateService(db);
        var result = await service.PreviewAsync(Request(Path.Combine(_tempDir, "does-not-exist"), library.PublicId, user.PublicId), default);

        Assert.False(result.Success);
        Assert.Equal("yac_db_not_found", result.Error);
    }

    [Fact]
    public async Task Preview_ReturnsErrorForUnknownLibrary()
    {
        var setup = await SetupAsync();
        var (db, library, user, node1, node2) = setup;

        var yacDir = BuildYacLibrary(_tempDir,
            (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 5, read: false, hasBeenOpened: true));

        var service = CreateService(db);
        var result = await service.PreviewAsync(Request(yacDir, OpaqueId.Encode(999999), user.PublicId), default);

        Assert.False(result.Success);
        Assert.Equal("library_not_found", result.Error);
    }

    [Fact]
    public async Task Preview_ReturnsErrorForUnknownUser()
    {
        var setup = await SetupAsync();
        var (db, library, user, node1, node2) = setup;

        var yacDir = BuildYacLibrary(_tempDir,
            (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 5, read: false, hasBeenOpened: true));

        var service = CreateService(db);
        var result = await service.PreviewAsync(Request(yacDir, library.PublicId, OpaqueId.Encode(999999)), default);

        Assert.False(result.Success);
        Assert.Equal("user_not_found", result.Error);
    }

    [Fact]
    public async Task Apply_PathWithBackslashes_MapsToNode()
    {
        var setup = await SetupAsync();
        var (db, library, user, node1, node2) = setup;

        // YACReader on Windows may store backslash paths; the importer normalizes.
        var yacDir = BuildYacLibrary(_tempDir,
            (1, @"/Series\Volume 1.cbz", "Volume 1.cbz", 3, read: true, hasBeenOpened: true));

        var service = CreateService(db);
        var result = await service.ApplyAsync(Request(yacDir, library.PublicId, user.PublicId), default);

        Assert.True(result.Success);
        Assert.Equal(1, result.Value!.Imported);
        var progress = await db.ReadingProgress.FirstAsync(p => p.ItemId == node1.Id);
        Assert.Equal((int)Core.Reading.ReadingState.Completed, progress.State);
        Assert.Equal(2, progress.Ordinal); // currentPage 3 → 0-based 2
    }

    [Fact]
    public async Task Apply_ClampsPageToPageCount()
    {
        var setup = await SetupAsync();
        var (db, library, user, node1, node2) = setup;

        // node1 has PageCount 10; report a currentPage beyond it.
        var yacDir = BuildYacLibrary(_tempDir,
            (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 99, read: false, hasBeenOpened: true));

        var service = CreateService(db);
        var result = await service.ApplyAsync(Request(yacDir, library.PublicId, user.PublicId), default);

        Assert.True(result.Success);
        var progress = await db.ReadingProgress.FirstAsync(p => p.ItemId == node1.Id);
        Assert.Equal(9, progress.Ordinal); // clamped to last page (0-based 9)
    }

    // 1.8.0 - deterministic race-recovery coverage. The 1.7.3 hardening wrapped
    // the bulk insert in a transaction with a per-collided-row retry, but the
    // recovery catch was not deterministically exercised (an EF interceptor
    // attempt was fragile due to EF Core batched-command behavior). These tests
    // use the BeforeBulkSaveAsync seam to seed a conflicting reading_progress
    // row via a real second DbContext - between the import's pre-check (which
    // saw no existing progress) and its bulk save - so the bulk insert collides
    // on the (UserId, ItemId) unique index and the recovery catch runs. EF Core
    // 10 returns only the colliding entry in DbUpdateException.Entries (verified
    // empirically), so the sibling row (node2) is left Added and inserts cleanly
    // on the retry - this also guards against a regression that wrongly detaches
    // a non-colliding sibling.

    /// <summary>
    /// Overwrite path: a concurrent progress write races the bulk insert; the
    /// recovery reloads the concurrent row and overwrites it with the import's
    /// state. No 500, one row per (user, item), sibling row still imports.
    /// </summary>
    [Fact]
    public async Task Apply_ConcurrentInsertRace_OverwritePath_UpdatesExistingRow()
    {
        var setup = await SetupAsync();
        var (db, library, user, node1, node2) = setup;

        // Both comics in-progress (no read marks) to focus on the progress race.
        var yacDir = BuildYacLibrary(_tempDir,
            (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 5, read: false, hasBeenOpened: true),
            (2, "/Series/Volume 2.cbz", "Volume 2.cbz", 20, read: false, hasBeenOpened: true));

        var service = CreateService(db);

        // Deterministic race: a concurrent progress write commits a row for
        // node1 AFTER the import's pre-check (which saw no existing progress)
        // but BEFORE the bulk save. The seam fires at exactly that point.
        service.BeforeBulkSaveAsync = async ct =>
        {
            await using var concurrent = new MangaPixerDbContext(_options);
            concurrent.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = user.Id,
                ItemId = node1.Id,
                ContentVersion = 1,
                EntryKey = OpaqueId.Encode(0),
                Ordinal = 0,
                NormalizedAnchor = 0.0,
                State = (int)Core.Reading.ReadingState.InProgress,
                Revision = 1,
                LastMutationId = "concurrent-write",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await concurrent.SaveChangesAsync(ct);
        };

        var result = await service.ApplyAsync(Request(yacDir, library.PublicId, user.PublicId, overwrite: true), default);

        Assert.True(result.Success, $"{result.Error}: {result.Message}");
        Assert.Equal(2, result.Value!.Imported);
        Assert.Equal(0, result.Value.Skipped);

        // One row per (user, item) - no duplicate slipped through, no 500.
        Assert.Equal(1, await db.ReadingProgress.CountAsync(p => p.UserId == user.Id && p.ItemId == node1.Id));
        Assert.Equal(1, await db.ReadingProgress.CountAsync(p => p.UserId == user.Id && p.ItemId == node2.Id));

        // Overwrite recovery: the concurrent row was reloaded and overwritten
        // with the import's state (mutation id + imported page position).
        var progress1 = await db.ReadingProgress.FirstAsync(p => p.ItemId == node1.Id);
        Assert.StartsWith("yacreader-import:", progress1.LastMutationId);
        Assert.Equal(4, progress1.Ordinal); // currentPage 5 -> 0-based 4
        Assert.Equal(2, progress1.Revision); // incremented by the overwrite recovery

        // Sibling row imported cleanly despite sharing the failed batch.
        var progress2 = await db.ReadingProgress.FirstAsync(p => p.ItemId == node2.Id);
        Assert.StartsWith("yacreader-import:", progress2.LastMutationId);
        Assert.Equal(19, progress2.Ordinal); // currentPage 20 -> 0-based 19
    }

    /// <summary>
    /// No-overwrite path: a concurrent progress write races the bulk insert; the
    /// recovery leaves the concurrent row as-is and counts the comic as skipped.
    /// No 500, one row per (user, item), sibling row still imports.
    /// </summary>
    [Fact]
    public async Task Apply_ConcurrentInsertRace_NoOverwritePath_SkipsAndCountsSkipped()
    {
        var setup = await SetupAsync();
        var (db, library, user, node1, node2) = setup;

        var yacDir = BuildYacLibrary(_tempDir,
            (1, "/Series/Volume 1.cbz", "Volume 1.cbz", 5, read: false, hasBeenOpened: true),
            (2, "/Series/Volume 2.cbz", "Volume 2.cbz", 20, read: false, hasBeenOpened: true));

        var service = CreateService(db);

        service.BeforeBulkSaveAsync = async ct =>
        {
            await using var concurrent = new MangaPixerDbContext(_options);
            concurrent.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = user.Id,
                ItemId = node1.Id,
                ContentVersion = 1,
                EntryKey = OpaqueId.Encode(0),
                Ordinal = 0,
                NormalizedAnchor = 0.0,
                State = (int)Core.Reading.ReadingState.InProgress,
                Revision = 1,
                LastMutationId = "concurrent-write",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await concurrent.SaveChangesAsync(ct);
        };

        var result = await service.ApplyAsync(Request(yacDir, library.PublicId, user.PublicId, overwrite: false), default);

        Assert.True(result.Success, $"{result.Error}: {result.Message}");
        Assert.Equal(1, result.Value!.Imported); // node2 only
        Assert.Equal(1, result.Value.Skipped);   // node1 raced and was skipped

        // One row per (user, item) - no duplicate, no 500.
        Assert.Equal(1, await db.ReadingProgress.CountAsync(p => p.UserId == user.Id && p.ItemId == node1.Id));
        Assert.Equal(1, await db.ReadingProgress.CountAsync(p => p.UserId == user.Id && p.ItemId == node2.Id));

        // No-overwrite recovery: the concurrent row is left untouched (import policy).
        var progress1 = await db.ReadingProgress.FirstAsync(p => p.ItemId == node1.Id);
        Assert.Equal("concurrent-write", progress1.LastMutationId);
        Assert.Equal(0, progress1.Ordinal); // unchanged
        Assert.Equal(1, progress1.Revision); // unchanged

        // Sibling row imported cleanly.
        var progress2 = await db.ReadingProgress.FirstAsync(p => p.ItemId == node2.Id);
        Assert.StartsWith("yacreader-import:", progress2.LastMutationId);
        Assert.Equal(19, progress2.Ordinal);
    }
}
