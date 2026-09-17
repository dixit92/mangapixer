namespace com.lifepixer.mangapixer.Tests.Server.Scanning;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Proves the natural-order sort key reaches the DATABASE through the real scanner
/// (1.15.0), not just through the encoder's own unit tests.
///
/// This is the gap the fix closes: <see cref="SortKey"/> existed and was correct, but
/// the scanner wrote the kind prefix followed by the raw display name, so every
/// ordering surface read a key under which "Chapter 10" precedes "Chapter 2". A test
/// that only exercised the encoder passed the whole time.
///
/// Real filesystem + real file-backed SQLite.
/// </summary>
public sealed class NaturalSortKeyScanTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _libRoot;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public NaturalSortKeyScanTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-sortkey-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(_libRoot);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "scan.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext db, long userId, long libraryId)> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var library = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Test Library",
            RootPath = _libRoot,
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

        return (db, user.Id, library.Id);
    }

    private async Task ScanAsync(MangaPixerDbContext db, long libraryId, long revision)
    {
        var coordinator = new LibraryScanCoordinator(
            db, new ReadOnlyLibraryFileSystem(_libRoot),
            new LibraryScanPolicy(), libraryId, revision, "test");
        var result = await coordinator.ScanAsync();
        Assert.True(result.Success, result.Error);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Every node the scanner persists carries the key the shared encoder produces for
    /// its (kind, display name). Asserting against <see cref="SortKey.ForNode"/> rather
    /// than a literal keeps the test honest if the on-disk format is revised again: what
    /// it pins is that production and the encoder cannot diverge.
    /// </summary>
    [Fact]
    public async Task Scan_PersistsEncoderProducedSortKeys_ForEveryNode()
    {
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series A", "Volume 10"));
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series A", "Volume 2"));
        File.WriteAllText(Path.Combine(_libRoot, "Series A", "Volume 10", "Chapter 100.cbz"), "a");
        File.WriteAllText(Path.Combine(_libRoot, "Series A", "Volume 2", "Chapter 2.cbz"), "b");
        File.WriteAllText(Path.Combine(_libRoot, "Series A", "Chapter 1.cbz"), "c");

        var (db, _, libraryId) = await SetupAsync();
        try
        {
            await ScanAsync(db, libraryId, 1);

            var nodes = await db.CatalogNodes
                .Where(n => n.LibraryId == libraryId)
                .Select(n => new { n.Kind, n.DisplayName, n.SortKey })
                .ToListAsync();

            Assert.NotEmpty(nodes);
            foreach (var node in nodes)
            {
                Assert.Equal(
                    SortKey.ForNode((CatalogNodeKind)node.Kind, node.DisplayName),
                    node.SortKey);
            }

            // And nothing is left in the pre-1.15.0 raw format (prefix + verbatim name).
            Assert.DoesNotContain(nodes, n => n.SortKey == $"{n.Kind}{n.DisplayName}" && ContainsDigit(n.DisplayName));
        }
        finally { await db.DisposeAsync(); }
    }

    /// <summary>
    /// The reported defect, end to end through the scanner and the browse service:
    /// chapters listed in numeric order, not "10" before "2".
    /// </summary>
    [Fact]
    public async Task Scan_ThenBrowse_ListsChaptersInNaturalOrder()
    {
        var series = Path.Combine(_libRoot, "Series");
        Directory.CreateDirectory(series);
        foreach (var n in new[] { 1, 2, 3, 10, 11, 20, 100 })
            File.WriteAllText(Path.Combine(series, $"Chapter {n}.cbz"), "x");

        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            await ScanAsync(db, libraryId, 1);

            var seriesNode = await db.CatalogNodes
                .FirstAsync(n => n.LibraryId == libraryId && n.DisplayName == "Series");

            var browse = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var page = await browse.BrowseAsync(userId, libraryId, seriesNode.Id, cursor: null, pageSize: 50, sort: "name");

            Assert.Equal(
                ["Chapter 1", "Chapter 2", "Chapter 3", "Chapter 10", "Chapter 11", "Chapter 20", "Chapter 100"],
                page.Items.Select(i => StripExtension(i.DisplayName)).ToArray());
        }
        finally { await db.DisposeAsync(); }
    }

    /// <summary>
    /// Digit-leading folder names sort where ordinal comparison puts them - ahead of the
    /// letters, not between "C" and "E" as the old 'D' digit-run marker would have.
    /// Folders still precede archives at the same level.
    /// </summary>
    [Fact]
    public async Task Scan_ThenBrowse_OrdersDigitLeadingNamesBeforeLetters()
    {
        foreach (var folder in new[] { "Akira", "10 Tigers", "2 Ninjas", "Zebra" })
            Directory.CreateDirectory(Path.Combine(_libRoot, folder));
        File.WriteAllText(Path.Combine(_libRoot, "1 Loose.cbz"), "x");
        File.WriteAllText(Path.Combine(_libRoot, "Aardvark.cbz"), "x");

        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            await ScanAsync(db, libraryId, 1);

            var browse = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var page = await browse.BrowseAsync(userId, libraryId, parentId: null, cursor: null, pageSize: 50, sort: "name");

            Assert.Equal(
                ["2 Ninjas", "10 Tigers", "Akira", "Zebra", "1 Loose", "Aardvark"],
                page.Items.Select(i => StripExtension(i.DisplayName)).ToArray());
        }
        finally { await db.DisposeAsync(); }
    }

    /// <summary>
    /// A rescan that re-points an existing node (the scanner's move/rename branch writes
    /// the sort key a second time, at a different call site than the insert branch) must
    /// produce the encoded key too - the original defect lived in the helper both call
    /// sites shared, so both need cover.
    /// </summary>
    [Fact]
    public async Task Rescan_AfterRename_StoresEncoderProducedSortKeys()
    {
        var series = Path.Combine(_libRoot, "Series 2");
        Directory.CreateDirectory(series);
        File.WriteAllText(Path.Combine(series, "Chapter 2.cbz"), "x");

        var (db, _, libraryId) = await SetupAsync();
        try
        {
            await ScanAsync(db, libraryId, 1);

            Directory.Move(series, Path.Combine(_libRoot, "Series 10"));
            File.WriteAllText(Path.Combine(_libRoot, "Series 10", "Chapter 10.cbz"), "y");
            await ScanAsync(db, libraryId, 2);

            var live = await db.CatalogNodes
                .Where(n => n.LibraryId == libraryId
                    && n.Availability != (int)CatalogNodeAvailability.Tombstoned)
                .Select(n => new { n.Kind, n.DisplayName, n.SortKey })
                .ToListAsync();

            Assert.NotEmpty(live);
            foreach (var node in live)
            {
                Assert.Equal(
                    SortKey.ForNode((CatalogNodeKind)node.Kind, node.DisplayName),
                    node.SortKey);
            }
        }
        finally { await db.DisposeAsync(); }
    }

    private static bool ContainsDigit(string value) => value.Any(char.IsDigit);

    private static string StripExtension(string displayName) =>
        displayName.EndsWith(".cbz", StringComparison.Ordinal)
            ? displayName[..^4]
            : displayName;
}
