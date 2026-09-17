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
using Xunit;

/// <summary>
/// Tests for the <c>BackfillNaturalSortKeys</c> migration (1.15.0).
///
/// Wiring the encoder into the scanner only fixes nodes discovered AFTER the upgrade.
/// Every row already in a deployed database still holds the pre-1.15.0 raw key, and a
/// catalog holding both formats orders worse than one holding either, because the two
/// are not comparable. The migration therefore has to recompute every existing row.
///
/// These tests run the migration the way a real upgrade does: build the schema at the
/// PREVIOUS migration, write rows in the old format, then migrate forward and check both
/// the stored keys and the ordering the browse service derives from them.
/// </summary>
public sealed class NaturalSortBackfillMigrationTests : IDisposable
{
    /// <summary>The migration immediately before the backfill - the "old build" schema point.</summary>
    private const string PreviousMigration = "20260914234823_AddHomeRecentWindowDays";
    private const string BackfillMigration = "20260917120000_BackfillNaturalSortKeys";

    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public NaturalSortBackfillMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-backfill-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "backfill.db");
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(_dbPath))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>The pre-1.15.0 key: kind digit followed by the display name verbatim.</summary>
    private static string LegacySortKey(CatalogNodeKind kind, string displayName) =>
        (kind == CatalogNodeKind.Folder ? "0" : "1") + displayName;

    private static MangaPixerDbContext NewContext(DbContextOptions<MangaPixerDbContext> options) => new(options);

    /// <summary>
    /// Builds the schema as an older build left it (migrated up to, but not past, the
    /// migration before the backfill) and seeds a library, a user and a chapter list
    /// whose sort keys are in the old raw format.
    /// </summary>
    private async Task<(long userId, long libraryId, long seriesId, string[] chapterNames)> SeedLegacyDatabaseAsync()
    {
        var chapterNames = new[] { "Chapter 1", "Chapter 2", "Chapter 3", "Chapter 10", "Chapter 11", "Chapter 100" };

        var db = NewContext(_options);
        await using (db)
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await DatabaseInitialization.ConfigureDatabaseAsync(db);

            var library = new LibraryEntity
            {
                PublicId = OpaqueId.Encode(1),
                DisplayName = "Legacy Library",
                RootPath = "/private/legacy",
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

            var series = new CatalogNodeEntity
            {
                PublicId = OpaqueId.Encode(10),
                LibraryId = library.Id,
                Kind = (int)CatalogNodeKind.Folder,
                DisplayName = "Series",
                RelativePath = "Series",
                PathKey = "Series",
                SortKey = LegacySortKey(CatalogNodeKind.Folder, "Series"),
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CatalogNodes.Add(series);
            await db.SaveChangesAsync();

            var id = 100L;
            foreach (var name in chapterNames)
            {
                db.CatalogNodes.Add(new CatalogNodeEntity
                {
                    PublicId = OpaqueId.Encode(id++),
                    LibraryId = library.Id,
                    ParentId = series.Id,
                    Kind = (int)CatalogNodeKind.Archive,
                    DisplayName = name,
                    RelativePath = $"Series/{name}",
                    PathKey = $"Series/{name}",
                    SortKey = LegacySortKey(CatalogNodeKind.Archive, name),
                    Availability = 0,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            await db.SaveChangesAsync();

            return (user.Id, library.Id, series.Id, chapterNames);
        }
    }

    /// <summary>
    /// Sanity check on the premise: with the legacy keys in place, the catalog really is
    /// mis-ordered. Without this the backfill assertions could pass against data that was
    /// already correct.
    /// </summary>
    [Fact]
    public async Task LegacyKeys_ProduceWrongOrder_BeforeTheMigration()
    {
        var (userId, libraryId, seriesId, _) = await SeedLegacyDatabaseAsync();

        var db = NewContext(_options);
        await using (db)
        {
            var browse = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var page = await browse.BrowseAsync(userId, libraryId, seriesId, cursor: null, pageSize: 50, sort: "name");

            // "Chapter 10" and "Chapter 100" sort between "Chapter 1" and "Chapter 2"
            // when the key is the raw name.
            Assert.Equal(
                ["Chapter 1", "Chapter 10", "Chapter 100", "Chapter 11", "Chapter 2", "Chapter 3"],
                page.Items.Select(i => i.DisplayName).ToArray());
        }
    }

    /// <summary>
    /// The deliverable: after migrating forward, every existing row holds the key the
    /// shared encoder produces, and browse returns natural order.
    /// </summary>
    [Fact]
    public async Task Migration_BackfillsExistingRows_AndBrowseOrdersNaturally()
    {
        var (userId, libraryId, seriesId, chapterNames) = await SeedLegacyDatabaseAsync();

        // Upgrade through the production entry point, not a bare MigrateAsync, so the
        // backup/adopt orchestration is exercised too.
        var db = NewContext(_options);
        await using (db)
        {
            await DatabaseInitialization.MigrateToLatestAsync(
                db, _tempDir, backupAsync: _ => Task.FromResult(true));
        }

        var verify = NewContext(_options);
        await using (verify)
        {
            var nodes = await verify.CatalogNodes
                .Select(n => new { n.Kind, n.DisplayName, n.SortKey })
                .ToListAsync();

            Assert.Equal(chapterNames.Length + 1, nodes.Count);
            foreach (var node in nodes)
            {
                Assert.Equal(
                    SortKey.ForNode((CatalogNodeKind)node.Kind, node.DisplayName),
                    node.SortKey);
            }

            var browse = new CatalogBrowseService(verify, new LibraryAuthorizationService(verify));
            var page = await browse.BrowseAsync(userId, libraryId, seriesId, cursor: null, pageSize: 50, sort: "name");

            Assert.Equal(chapterNames, page.Items.Select(i => i.DisplayName).ToArray());
        }
    }

    /// <summary>
    /// Rows written after the upgrade and rows rewritten by the backfill must be in the
    /// SAME format, or keyset pagination over a mixed catalog silently skips items. Adding
    /// a node through the entity model (the shape the scanner persists) and re-reading the
    /// whole listing is the check that matters.
    /// </summary>
    [Fact]
    public async Task Migration_LeavesBackfilledAndNewRowsComparable()
    {
        var (userId, libraryId, seriesId, _) = await SeedLegacyDatabaseAsync();

        var migrate = NewContext(_options);
        await using (migrate)
        {
            await DatabaseInitialization.MigrateToLatestAsync(
                migrate, _tempDir, backupAsync: _ => Task.FromResult(true));

            // A "newly scanned" chapter, keyed the way LibraryScanCoordinator keys it.
            migrate.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = OpaqueId.Encode(999),
                LibraryId = libraryId,
                ParentId = seriesId,
                Kind = (int)CatalogNodeKind.Archive,
                DisplayName = "Chapter 20",
                RelativePath = "Series/Chapter 20",
                PathKey = "Series/Chapter 20",
                SortKey = SortKey.ForNode(CatalogNodeKind.Archive, "Chapter 20"),
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await migrate.SaveChangesAsync();
        }

        var verify = NewContext(_options);
        await using (verify)
        {
            var browse = new CatalogBrowseService(verify, new LibraryAuthorizationService(verify));
            var page = await browse.BrowseAsync(userId, libraryId, seriesId, cursor: null, pageSize: 50, sort: "name");

            Assert.Equal(
                ["Chapter 1", "Chapter 2", "Chapter 3", "Chapter 10", "Chapter 11", "Chapter 20", "Chapter 100"],
                page.Items.Select(i => i.DisplayName).ToArray());
        }
    }

    /// <summary>
    /// The backfill is a recompute, so running it twice must be a no-op. Guarded by the
    /// migration's WHERE clause; re-applying it by hand is the cheapest way to prove it.
    /// </summary>
    [Fact]
    public async Task Migration_IsIdempotent_WhenReapplied()
    {
        await SeedLegacyDatabaseAsync();

        var db = NewContext(_options);
        await using (db)
        {
            await DatabaseInitialization.MigrateToLatestAsync(
                db, _tempDir, backupAsync: _ => Task.FromResult(true));

            var before = await db.CatalogNodes
                .OrderBy(n => n.Id)
                .Select(n => n.SortKey)
                .ToListAsync();

            var rewritten = await db.Database.ExecuteSqlRawAsync(
                "UPDATE catalog_nodes SET SortKey = mp_sort_key(Kind, DisplayName) WHERE SortKey <> mp_sort_key(Kind, DisplayName);");
            Assert.Equal(0, rewritten);

            var after = await db.CatalogNodes
                .AsNoTracking()
                .OrderBy(n => n.Id)
                .Select(n => n.SortKey)
                .ToListAsync();

            Assert.Equal(before, after);
        }
    }

    /// <summary>
    /// A fresh install migrates from empty through the backfill without incident (the
    /// UPDATE simply matches no rows), and ends with no migration pending.
    /// </summary>
    [Fact]
    public async Task Migration_OnFreshDatabase_AppliesCleanly()
    {
        var db = NewContext(_options);
        await using (db)
        {
            await DatabaseInitialization.MigrateToLatestAsync(
                db, _tempDir, backupAsync: _ => Task.FromResult(true));

            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains(BackfillMigration, await db.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await db.CatalogNodes.ToListAsync());
        }
    }

    /// <summary>
    /// Rolling back restores the legacy raw key. The project is forward-only, so this is a
    /// safety net rather than a supported path - but a Down that throws would block
    /// anyone stepping a development database backwards.
    /// </summary>
    [Fact]
    public async Task Migration_Down_RestoresLegacyKeys()
    {
        var (_, _, _, chapterNames) = await SeedLegacyDatabaseAsync();

        var db = NewContext(_options);
        await using (db)
        {
            await DatabaseInitialization.MigrateToLatestAsync(
                db, _tempDir, backupAsync: _ => Task.FromResult(true));

            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            var keys = await db.CatalogNodes
                .AsNoTracking()
                .Where(n => n.Kind == (int)CatalogNodeKind.Archive)
                .Select(n => n.SortKey)
                .ToListAsync();

            Assert.Equal(
                chapterNames.Select(n => LegacySortKey(CatalogNodeKind.Archive, n)).OrderBy(k => k, StringComparer.Ordinal),
                keys.OrderBy(k => k, StringComparer.Ordinal));
        }
    }
}
