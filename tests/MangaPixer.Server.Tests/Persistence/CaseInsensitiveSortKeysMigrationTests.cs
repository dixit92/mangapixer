namespace com.lifepixer.mangapixer.Tests.Server.Persistence;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Linq;
using Xunit;

/// <summary>
/// Tests for the <c>CaseInsensitiveSortKeys</c> data migration. Like the 1.15.0 backfill
/// (<see cref="NaturalSortBackfillMigrationTests"/>), the new encoder only fixes nodes the
/// scanner writes after the upgrade; every existing row holds the case-sensitive key, and a
/// catalog mixing both formats orders worse than either. These tests build the schema at the
/// PREVIOUS migration, write rows in the old format, migrate forward and check the stored keys
/// and the browse order, then check idempotence and the Down path.
/// </summary>
public sealed class CaseInsensitiveSortKeysMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260925043725_AddMetadataFoundations";
    private const string CaseMigration = "20260926120000_CaseInsensitiveSortKeys";

    /// <summary>Names in the order case-insensitive sort must produce.</summary>
    private static readonly string[] NamesInOrder = ["apple.cbz", "Banana.cbz", "banana.cbz", "cherry 2.cbz", "Cherry 10.cbz", "Zebra.cbz"];

    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public CaseInsensitiveSortKeysMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-casekeys-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "casekeys.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>The previous (case-sensitive natural) key: kind digit + the name encoded as spelled.</summary>
    private static string PreviousSortKey(CatalogNodeKind kind, string displayName) =>
        (kind == CatalogNodeKind.Folder ? "0" : "1") + SortKey.EncodeName(displayName);

    /// <summary>
    /// Schema at the previous migration (identical to the current model - this migration is
    /// data-only), a library, an admin and a series whose archive keys are in the old format.
    /// </summary>
    private async Task<(long userId, long libraryId, long seriesId)> SeedPreviousFormatAsync()
    {
        await using var db = new MangaPixerDbContext(_options);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var library = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Case Library",
            RootPath = "/private/case",
            CreatedAt = DateTimeOffset.UtcNow,
        };
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
        db.Libraries.Add(library);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var series = NewNode(library.Id, null, CatalogNodeKind.Folder, "Series", 10);
        db.CatalogNodes.Add(series);
        await db.SaveChangesAsync();

        var publicId = 100L;
        // Insertion order is deliberately not sorted order.
        foreach (var name in new[] { "Zebra.cbz", "banana.cbz", "Cherry 10.cbz", "apple.cbz", "cherry 2.cbz", "Banana.cbz" })
            db.CatalogNodes.Add(NewNode(library.Id, series.Id, CatalogNodeKind.Archive, name, publicId++));
        await db.SaveChangesAsync();

        return (user.Id, library.Id, series.Id);
    }

    private static CatalogNodeEntity NewNode(long libraryId, long? parentId, CatalogNodeKind kind, string name, long publicId) =>
        new()
        {
            PublicId = OpaqueId.Encode(publicId),
            LibraryId = libraryId,
            ParentId = parentId,
            Kind = (int)kind,
            DisplayName = name,
            RelativePath = parentId is null ? name : $"Series/{name}",
            PathKey = parentId is null ? name : $"Series/{name}",
            SortKey = PreviousSortKey(kind, name),
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private async Task<string[]> BrowseNamesAsync(long userId, long libraryId, long seriesId)
    {
        await using var db = new MangaPixerDbContext(_options);
        var browse = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
        var page = await browse.BrowseAsync(userId, libraryId, seriesId, cursor: null, pageSize: 50, sort: "name");
        return page.Items.Select(i => i.DisplayName).ToArray();
    }

    /// <summary>Premise check: with the old keys every capitalised name lists first.</summary>
    [Fact]
    public async Task PreviousKeys_ListCapitalisedNamesFirst_BeforeTheMigration()
    {
        var (userId, libraryId, seriesId) = await SeedPreviousFormatAsync();

        Assert.Equal(
            ["Banana.cbz", "Cherry 10.cbz", "Zebra.cbz", "apple.cbz", "banana.cbz", "cherry 2.cbz"],
            await BrowseNamesAsync(userId, libraryId, seriesId));
    }

    [Fact]
    public async Task Migration_RewritesExistingRows_AndBrowseIsCaseInsensitive()
    {
        var (userId, libraryId, seriesId) = await SeedPreviousFormatAsync();

        await using (var db = new MangaPixerDbContext(_options))
        {
            await DatabaseInitialization.MigrateToLatestAsync(db, _tempDir, backupAsync: _ => Task.FromResult(true));
            Assert.Contains(CaseMigration, await db.Database.GetAppliedMigrationsAsync());

            var nodes = await db.CatalogNodes.Select(n => new { n.Kind, n.DisplayName, n.SortKey }).ToListAsync();
            foreach (var node in nodes)
                Assert.Equal(SortKey.ForNode((CatalogNodeKind)node.Kind, node.DisplayName), node.SortKey);
        }

        Assert.Equal(NamesInOrder, await BrowseNamesAsync(userId, libraryId, seriesId));
    }

    [Fact]
    public async Task Migration_IsIdempotent_WhenReapplied()
    {
        await SeedPreviousFormatAsync();

        await using var db = new MangaPixerDbContext(_options);
        await DatabaseInitialization.MigrateToLatestAsync(db, _tempDir, backupAsync: _ => Task.FromResult(true));

        var rewritten = await db.Database.ExecuteSqlRawAsync(
            "UPDATE catalog_nodes SET SortKey = mp_sort_key(Kind, DisplayName) WHERE SortKey <> mp_sort_key(Kind, DisplayName);");
        Assert.Equal(0, rewritten);
    }

    /// <summary>Down restores exactly the previous case-sensitive keys, without the SQL function.</summary>
    [Fact]
    public async Task Migration_Down_RestoresPreviousKeys()
    {
        await SeedPreviousFormatAsync();

        await using var db = new MangaPixerDbContext(_options);
        await DatabaseInitialization.MigrateToLatestAsync(db, _tempDir, backupAsync: _ => Task.FromResult(true));
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

        var nodes = await db.CatalogNodes.AsNoTracking().Select(n => new { n.Kind, n.DisplayName, n.SortKey }).ToListAsync();
        Assert.NotEmpty(nodes);
        foreach (var node in nodes)
            Assert.Equal(PreviousSortKey((CatalogNodeKind)node.Kind, node.DisplayName), node.SortKey);
    }
}
