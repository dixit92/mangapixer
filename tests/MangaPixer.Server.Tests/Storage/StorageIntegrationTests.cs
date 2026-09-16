namespace com.lifepixer.mangapixer.Tests.Server.Storage;

using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Integration tests for the read-only filesystem and library registration.
/// Uses synthetic filesystem trees in temp directories.
/// </summary>
public sealed class StorageIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public StorageIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-storage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void ReadOnlyFileSystem_RootExists_ReturnsTrueForValidRoot()
    {
        var libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(libRoot);

        var fs = new ReadOnlyLibraryFileSystem(libRoot);
        Assert.True(fs.RootExists());
    }

    [Fact]
    public void ReadOnlyFileSystem_RootExists_ReturnsFalseForMissingRoot()
    {
        var fs = new ReadOnlyLibraryFileSystem(Path.Combine(_tempDir, "nonexistent"));
        Assert.False(fs.RootExists());
    }

    [Fact]
    public void ReadOnlyFileSystem_EnumerateEntries_ReturnsChildren()
    {
        var libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(libRoot);
        Directory.CreateDirectory(Path.Combine(libRoot, "subfolder"));
        File.WriteAllText(Path.Combine(libRoot, "archive.cbz"), "fake");
        File.WriteAllText(Path.Combine(libRoot, "image.png"), "fake");

        var fs = new ReadOnlyLibraryFileSystem(libRoot);
        var entries = fs.EnumerateEntries("");

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.Name == "subfolder" && e.Kind == EntryKind.Directory);
        Assert.Contains(entries, e => e.Name == "archive.cbz" && e.Kind == EntryKind.File);
        Assert.Contains(entries, e => e.Name == "image.png" && e.Kind == EntryKind.File);
    }

    [Fact]
    public void ReadOnlyFileSystem_OpenRead_ReturnsReadOnlyStream()
    {
        var libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(libRoot);
        var filePath = Path.Combine(libRoot, "test.txt");
        File.WriteAllText(filePath, "hello world");

        var fs = new ReadOnlyLibraryFileSystem(libRoot);
        using var stream = fs.OpenRead("test.txt");

        Assert.False(stream.CanWrite);
        Assert.True(stream.CanRead);

        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        Assert.Equal("hello world", content);
    }

    [Fact]
    public void ReadOnlyFileSystem_PathTraversal_Rejected()
    {
        var libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(libRoot);
        var secretPath = Path.Combine(_tempDir, "secret.txt");
        File.WriteAllText(secretPath, "secret");

        var fs = new ReadOnlyLibraryFileSystem(libRoot);

        // Try to escape the root via ../
        Assert.Null(fs.GetEntry("../secret.txt"));
        Assert.Throws<FileNotFoundException>(() => fs.OpenRead("../secret.txt"));
    }

    [Fact]
    public void ReadOnlyFileSystem_GetSourceStamp_ReturnsCorrectValues()
    {
        var libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(libRoot);
        var filePath = Path.Combine(libRoot, "test.txt");
        File.WriteAllText(filePath, "hello world");
        var info = new FileInfo(filePath);

        var fs = new ReadOnlyLibraryFileSystem(libRoot);
        var stamp = fs.GetSourceStamp("test.txt");

        Assert.Equal(info.Length, stamp.ByteLength);
        Assert.Equal(info.LastWriteTimeUtc.Ticks, stamp.LastWriteTicks);
    }

    [Fact]
    public void ReadOnlyFileSystem_UnicodeAndSpaces_Supported()
    {
        var libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(libRoot);
        var subDir = Path.Combine(libRoot, "第1巻 漫画");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "page 001.png"), "fake");

        var fs = new ReadOnlyLibraryFileSystem(libRoot);
        var entries = fs.EnumerateEntries("");

        Assert.Contains(entries, e => e.Name == "第1巻 漫画" && e.Kind == EntryKind.Directory);

        var subEntries = fs.EnumerateEntries("第1巻 漫画");
        Assert.Contains(subEntries, e => e.Name == "page 001.png" && e.Kind == EntryKind.File);
    }

    [Fact]
    public async Task LibraryRegistration_ValidRoot_Succeeds()
    {
        var libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(libRoot);

        using var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var service = new LibraryRegistrationService(db, new AppRootOptions { DataRoot = Path.Combine(_tempDir, "data") });
        var result = await service.RegisterAsync("Test Library", libRoot);

        Assert.True(result.Success);
        Assert.NotNull(result.Library);
        Assert.Equal("Test Library", result.Library!.DisplayName);
    }

    [Fact]
    public async Task LibraryRegistration_DuplicateRoot_Rejected()
    {
        var libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(libRoot);

        using var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var service = new LibraryRegistrationService(db, new AppRootOptions { DataRoot = Path.Combine(_tempDir, "data") });

        var result1 = await service.RegisterAsync("Library 1", libRoot);
        Assert.True(result1.Success);

        var result2 = await service.RegisterAsync("Library 2", libRoot);
        Assert.False(result2.Success);
        Assert.Equal("duplicate_root", result2.Error);
    }

    [Fact]
    public async Task LibraryRegistration_NestedRoot_Rejected()
    {
        var libRoot = Path.Combine(_tempDir, "library");
        var nestedRoot = Path.Combine(libRoot, "nested");
        Directory.CreateDirectory(libRoot);
        Directory.CreateDirectory(nestedRoot);

        using var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var service = new LibraryRegistrationService(db, new AppRootOptions { DataRoot = Path.Combine(_tempDir, "data") });

        var result1 = await service.RegisterAsync("Parent", libRoot);
        Assert.True(result1.Success);

        var result2 = await service.RegisterAsync("Nested", nestedRoot);
        Assert.False(result2.Success);
        Assert.Equal("nested_root", result2.Error);
    }

    [Fact]
    public async Task LibraryRegistration_AppRootConflict_Rejected()
    {
        var dataRoot = Path.Combine(_tempDir, "data");
        Directory.CreateDirectory(dataRoot);

        using var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var service = new LibraryRegistrationService(db, new AppRootOptions { DataRoot = dataRoot });

        // Try to register the data root as a library
        var result = await service.RegisterAsync("Data Library", dataRoot);
        Assert.False(result.Success);
        Assert.Equal("app_root_conflict", result.Error);
    }

    [Fact]
    public async Task LibraryRegistration_NonExistentRoot_Rejected()
    {
        using var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var service = new LibraryRegistrationService(db, new AppRootOptions { DataRoot = Path.Combine(_tempDir, "data") });

        var result = await service.RegisterAsync("Missing", Path.Combine(_tempDir, "nonexistent"));
        Assert.False(result.Success);
        Assert.Equal("root_not_found", result.Error);
    }

    [Fact]
    public void LibraryScanPolicy_IsApprovedArchive_RecognizesExtensions()
    {
        var policy = new LibraryScanPolicy();

        Assert.True(policy.IsApprovedArchive("manga.cbz"));
        Assert.True(policy.IsApprovedArchive("manga.CBR"));
        Assert.True(policy.IsApprovedArchive("manga.7z"));
        Assert.True(policy.IsApprovedArchive("manga.zip"));
        Assert.False(policy.IsApprovedArchive("manga.pdf"));
        Assert.False(policy.IsApprovedArchive("manga.txt"));
    }

    [Fact]
    public void LibraryScanPolicy_IsIgnoredDirectory_RecognizesBookkeeping()
    {
        var policy = new LibraryScanPolicy();

        Assert.True(policy.IsIgnoredDirectory(".yacreader"));
        Assert.True(policy.IsIgnoredDirectory(".yacreaderlibrary")); // not in the name set, but hidden
        Assert.True(policy.IsIgnoredDirectory("$RECYCLE.BIN"));
        Assert.True(policy.IsIgnoredDirectory(".git"));
        Assert.True(policy.IsIgnoredDirectory(".manga_collection")); // hidden dirs are ignored (user requirement)
        Assert.False(policy.IsIgnoredDirectory("My Manga"));
    }

    [Fact]
    public void LibraryScanPolicy_IsIgnoredFile_RecognizesMetadata()
    {
        var policy = new LibraryScanPolicy();

        Assert.True(policy.IsIgnoredFile(".DS_Store"));
        Assert.True(policy.IsIgnoredFile("ComicInfo.xml"));
        Assert.True(policy.IsIgnoredFile("thumbs.db"));
        Assert.False(policy.IsIgnoredFile("page001.png"));
    }

    [Fact]
    public void LibraryScanPolicy_IsArchiveCandidate_FiltersCorrectly()
    {
        var policy = new LibraryScanPolicy();

        var archiveEntry = new FileSystemEntry
        {
            RelativePath = "manga.cbz",
            Name = "manga.cbz",
            Kind = EntryKind.File,
            ByteLength = 100,
            LastWriteTimeUtc = DateTimeOffset.UtcNow,
            IsHidden = false,
            IsSymlink = false,
        };
        Assert.True(policy.IsArchiveCandidate(archiveEntry));

        var metaEntry = new FileSystemEntry
        {
            RelativePath = "ComicInfo.xml",
            Name = "ComicInfo.xml",
            Kind = EntryKind.File,
            ByteLength = 100,
            LastWriteTimeUtc = DateTimeOffset.UtcNow,
            IsHidden = false,
            IsSymlink = false,
        };
        Assert.False(policy.IsArchiveCandidate(metaEntry));

        var dirEntry = new FileSystemEntry
        {
            RelativePath = "folder",
            Name = "folder",
            Kind = EntryKind.Directory,
            ByteLength = 0,
            LastWriteTimeUtc = DateTimeOffset.UtcNow,
            IsHidden = false,
            IsSymlink = false,
        };
        Assert.False(policy.IsArchiveCandidate(dirEntry));
    }

    [Fact]
    public void LibraryScanPolicy_ShouldTraverseDirectory_FiltersCorrectly()
    {
        var policy = new LibraryScanPolicy();

        var normalDir = new FileSystemEntry
        {
            RelativePath = "manga",
            Name = "manga",
            Kind = EntryKind.Directory,
            ByteLength = 0,
            LastWriteTimeUtc = DateTimeOffset.UtcNow,
            IsHidden = false,
            IsSymlink = false,
        };
        Assert.True(policy.ShouldTraverseDirectory(normalDir));

        var ignoredDir = new FileSystemEntry
        {
            RelativePath = ".git",
            Name = ".git",
            Kind = EntryKind.Directory,
            ByteLength = 0,
            LastWriteTimeUtc = DateTimeOffset.UtcNow,
            IsHidden = false,
            IsSymlink = false,
        };
        Assert.False(policy.ShouldTraverseDirectory(ignoredDir));

        var symlinkDir = new FileSystemEntry
        {
            RelativePath = "link",
            Name = "link",
            Kind = EntryKind.Directory,
            ByteLength = 0,
            LastWriteTimeUtc = DateTimeOffset.UtcNow,
            IsHidden = false,
            IsSymlink = true,
        };
        Assert.False(policy.ShouldTraverseDirectory(symlinkDir));
    }

    [Fact]
    public async Task LibraryRegistration_Delete_RemovesRecord()
    {
        var libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(libRoot);

        using var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var service = new LibraryRegistrationService(db, new AppRootOptions { DataRoot = Path.Combine(_tempDir, "data") });
        var result = await service.RegisterAsync("Test", libRoot);
        Assert.True(result.Success);

        var deleted = await service.DeleteAsync(result.Library!.Id);
        Assert.True(deleted);

        // Verify the library directory still exists (no file deletion — source-media invariant)
        Assert.True(Directory.Exists(libRoot));

        // Verify the database record is gone
        var count = await db.Libraries.CountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task LibraryRegistration_Delete_RemovesAllMetadataAndThumbnails_LeavesSourceFiles()
    {
        var libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(libRoot);
        // A source file that must survive the delete (source-media invariant).
        var sourceArchive = Path.Combine(libRoot, "vol1.cbz");
        File.WriteAllText(sourceArchive, "source-bytes");

        using var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        // A user to attach per-user loose references to.
        var user = new UserEntity
        {
            PublicId = "u1",
            UserName = "reader",
            NormalizedUserName = "READER",
            PasswordHash = "hash",
            SecurityStamp = "stamp",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);

        var service = new LibraryRegistrationService(
            db,
            new AppRootOptions { DataRoot = Path.Combine(_tempDir, "data") });
        var reg = await service.RegisterAsync("Test", libRoot);
        Assert.True(reg.Success);
        var library = reg.Library!;

        // A folder node + an archive item node (Kind == 1).
        var folder = new CatalogNodeEntity
        {
            PublicId = "folder1",
            LibraryId = library.Id,
            Kind = 0,
            DisplayName = "Series",
            RelativePath = "Series",
            PathKey = "series",
            SortKey = "0series",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(folder);
        await db.SaveChangesAsync();

        var item = new CatalogNodeEntity
        {
            PublicId = "item1",
            LibraryId = library.Id,
            ParentId = folder.Id,
            Kind = 1,
            DisplayName = "vol1",
            RelativePath = "Series/vol1.cbz",
            PathKey = "series/vol1.cbz",
            SortKey = "0vol1",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(item);
        await db.SaveChangesAsync();

        var archiveItem = new ArchiveItemEntity
        {
            NodeId = item.Id,
            ContentVersion = 3,
            AnalysisState = 0,
            ThumbnailState = 1,
            ThumbnailContentVersion = 3,
        };
        db.ArchiveItems.Add(archiveItem);

        var page = new PageEntryEntity
        {
            ItemId = item.Id,
            ContentVersion = 3,
            Ordinal = 0,
            EntryKey = "p0",
            SourceEntryLocator = "loc",
            MediaType = "image/webp",
            ByteSize = 10,
        };
        db.PageEntries.Add(page);

        // Loose references keyed by ItemId (no FK).
        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = user.Id,
            ItemId = item.Id,
            ContentVersion = 3,
            EntryKey = "p0",
            Ordinal = 0,
            State = 1,
            Revision = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.ReadMarks.Add(new ReadMarkEntity
        {
            UserId = user.Id,
            ItemId = item.Id,
            MarkedAt = DateTimeOffset.UtcNow,
            Source = "manual",
        });
        db.Bookmarks.Add(new BookmarkEntity
        {
            UserId = user.Id,
            ItemId = item.Id,
            ContentVersion = 3,
            EntryKey = "p0",
            Ordinal = 0,
            NormalizedAnchor = 0.5,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.ItemReaderOverrides.Add(new ItemReaderOverridesEntity
        {
            UserId = user.Id,
            ItemId = item.Id,
            ReaderMode = 2,
        });

        // Loose references keyed by LibraryId (no FK).
        db.ScanRuns.Add(new ScanRunEntity
        {
            LibraryId = library.Id,
            ScanRevision = 1,
            Status = 2,
            StartedAt = DateTimeOffset.UtcNow,
        });
        db.ScanObservations.Add(new ScanObservationEntity
        {
            ScanRunId = 1,
            LibraryId = library.Id,
            RelativePath = "Series",
            PathKey = "series",
            Kind = 0,
            DisplayName = "Series",
            ParentPathKey = "",
        });
        db.LibraryGrants.Add(new LibraryGrantEntity
        {
            UserId = user.Id,
            LibraryId = library.Id,
            GrantedAt = DateTimeOffset.UtcNow,
        });
        db.Jobs.Add(new JobEntity
        {
            JobType = "analyze",
            LibraryId = library.Id,
            ItemId = item.Id,
            Status = 2,
            QueuedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();

        // FTS row for the item node (the AFTER-DELETE trigger does not fire on cascade).
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO catalog_search (display_name, relative_path, library_id, node_id) VALUES ('vol1', 'Series/vol1.cbz', {library.Id}, {item.Id})");

        // Durable thumbnail file on disk for the item.
        var thumbRoot = Path.Combine(_tempDir, "data", "thumbnails");
        var thumbnailStore = new ThumbnailStore(thumbRoot);
        await thumbnailStore.PublishAsync(item.Id, 3, WriteTempImage(), default);
        Assert.True(thumbnailStore.HasThumbnail(item.Id, 3));

        // Capture ids, then drop the seeding context so the delete runs against a
        // fresh change tracker — mirroring the production DI scope where the
        // service's DbContext has no tracked children from a prior seeding.
        // (ExecuteDeleteAsync bypasses the tracker; if tracked children remain,
        // SaveChanges would try to re-delete them and throw a concurrency error.)
        var libraryId = library.Id;
        var itemId = item.Id;
        await db.DisposeAsync();

        // Delete the library with a fresh context + the durable thumbnail store.
        using var db2 = new MangaPixerDbContext(_options);
        var serviceWithStore = new LibraryRegistrationService(
            db2,
            new AppRootOptions { DataRoot = Path.Combine(_tempDir, "data") },
            thumbnailStore);
        var deleted = await serviceWithStore.DeleteAsync(libraryId);
        Assert.True(deleted);

        // Source files untouched (source-media invariant).
        Assert.True(File.Exists(sourceArchive));

        // Durable thumbnail file removed.
        Assert.False(thumbnailStore.HasThumbnail(itemId, 3));

        // Library + cascaded rows gone.
        Assert.Equal(0, await db2.Libraries.CountAsync());
        Assert.Equal(0, await db2.CatalogNodes.CountAsync());
        Assert.Equal(0, await db2.ArchiveItems.CountAsync());
        Assert.Equal(0, await db2.PageEntries.CountAsync());

        // Loose-reference rows by ItemId gone.
        Assert.Equal(0, await db2.ReadingProgress.CountAsync());
        Assert.Equal(0, await db2.ReadMarks.CountAsync());
        Assert.Equal(0, await db2.Bookmarks.CountAsync());
        Assert.Equal(0, await db2.ItemReaderOverrides.CountAsync());

        // Loose-reference rows by LibraryId gone.
        Assert.Equal(0, await db2.ScanRuns.CountAsync());
        Assert.Equal(0, await db2.ScanObservations.CountAsync());
        Assert.Equal(0, await db2.LibraryGrants.CountAsync());
        Assert.Equal(0, await db2.Jobs.CountAsync());

        // FTS row gone (cascade did not fire the trigger; explicit delete did).
        var ftsRows = await db2.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS Value FROM catalog_search WHERE library_id = {libraryId}").FirstAsync();
        Assert.Equal(0, ftsRows);

        // The user survives (delete is library-scoped, not user-scoped).
        Assert.Equal(1, await db2.Users.CountAsync());
    }

    private static string WriteTempImage()
    {
        var path = Path.Combine(Path.GetTempPath(), "mangapixer-thumb-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        return path;
    }
}
