namespace com.lifepixer.mangaplex.Tests.Server.Storage;

using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Storage;
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
    private readonly DbContextOptions<MangaPlexDbContext> _options;

    public StorageIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-storage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPlexDbContext>()
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

        using var db = new MangaPlexDbContext(_options);
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

        using var db = new MangaPlexDbContext(_options);
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

        using var db = new MangaPlexDbContext(_options);
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

        using var db = new MangaPlexDbContext(_options);
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
        using var db = new MangaPlexDbContext(_options);
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
    public async Task LibraryRegistration_Unregister_RemovesRecord()
    {
        var libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(libRoot);

        using var db = new MangaPlexDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var service = new LibraryRegistrationService(db, new AppRootOptions { DataRoot = Path.Combine(_tempDir, "data") });
        var result = await service.RegisterAsync("Test", libRoot);
        Assert.True(result.Success);

        var unregistered = await service.UnregisterAsync(result.Library!.Id);
        Assert.True(unregistered);

        // Verify the library directory still exists (no file deletion)
        Assert.True(Directory.Exists(libRoot));

        // Verify the database record is gone
        var count = await db.Libraries.CountAsync();
        Assert.Equal(0, count);
    }
}
