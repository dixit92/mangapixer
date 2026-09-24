namespace com.lifepixer.mangapixer.Tests.Server.Features.Reading;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for the shared per-archive double-page layout (1.23.0).
/// Real file-backed SQLite so the FK cascade and the set-based stale delete behave as
/// in production. Covers save/load, explicit-empty vs none, overwrite, the
/// ContentVersion stamp (stale layout ignored + lazily deleted, stale write rejected),
/// authorization (grant / no grant / inactive), not-ready and folder items, and the
/// cascade on node delete. <see cref="SpreadLayoutService.Validate"/> is covered as a unit.
/// </summary>
public sealed class SpreadLayoutServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public SpreadLayoutServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-spread-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        var connectionString = DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "spread.db"));
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<MangaPixerDbContext> NewDbAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        return db;
    }

    private static SpreadLayoutService NewService(MangaPixerDbContext db) =>
        new(db, new LibraryAuthorizationService(db));

    private static async Task<long> AddUserAsync(MangaPixerDbContext db, string name, bool admin, bool active = true)
    {
        var user = new UserEntity
        {
            PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            IsActive = active,
            IsAdmin = admin,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private sealed record Seed(long AdminId, long LibraryId, CatalogNodeEntity Archive);

    /// <summary>A library with one analyzed 10-page archive at content version 1.</summary>
    private static async Task<Seed> SeedAsync(MangaPixerDbContext db, int pageCount = 10, int analysisState = 0)
    {
        var adminId = await AddUserAsync(db, "admin", admin: true);
        var lib = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
            DisplayName = "Lib",
            RootPath = "/synthetic/lib",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();

        var node = await AddNodeAsync(db, lib.Id, CatalogNodeKind.Archive, "vol01.cbz");
        db.ArchiveItems.Add(new ArchiveItemEntity
        {
            NodeId = node.Id,
            ContentVersion = 1,
            AnalysisState = analysisState,
            PageCount = pageCount,
            ByteLength = 1024,
            ModificationTicks = 1,
        });
        await db.SaveChangesAsync();
        return new Seed(adminId, lib.Id, node);
    }

    private static async Task<CatalogNodeEntity> AddNodeAsync(
        MangaPixerDbContext db, long libraryId, CatalogNodeKind kind, string name)
    {
        var node = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
            LibraryId = libraryId,
            Kind = (int)kind,
            DisplayName = name,
            RelativePath = name,
            PathKey = name,
            SortKey = name,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node;
    }

    [Fact]
    public async Task Save_ThenLoad_ReturnsTheForcedStarts()
    {
        await using var db = await NewDbAsync();
        var seed = await SeedAsync(db);
        var service = NewService(db);

        var result = await service.SetAsync(seed.AdminId, seed.Archive.Id, seed.Archive.PublicId, 1, [1, 5, 9]);

        Assert.Equal(SpreadLayoutService.SetStatus.Ok, result.Status);
        Assert.Equal([1, 5, 9], result.Layout!.SpreadStarts);
        Assert.Equal(1, result.Layout.ContentVersion);
        Assert.Equal(seed.Archive.PublicId, result.Layout.ItemId);

        await using var fresh = new MangaPixerDbContext(_options);
        Assert.Equal([1, 5, 9], await NewService(fresh).GetCurrentAsync(seed.Archive.Id, 1, 10));
    }

    [Fact]
    public async Task NoSavedLayout_LoadsAsNull_ButExplicitEmpty_LoadsAsEmpty()
    {
        await using var db = await NewDbAsync();
        var seed = await SeedAsync(db);
        var service = NewService(db);

        Assert.Null(await service.GetCurrentAsync(seed.Archive.Id, 1, 10));

        var result = await service.SetAsync(seed.AdminId, seed.Archive.Id, seed.Archive.PublicId, 1, []);
        Assert.Equal(SpreadLayoutService.SetStatus.Ok, result.Status);

        var loaded = await service.GetCurrentAsync(seed.Archive.Id, 1, 10);
        Assert.NotNull(loaded);
        Assert.Empty(loaded!);
    }

    [Fact]
    public async Task SecondSave_ReplacesTheLayout_OneRowPerArchive()
    {
        await using var db = await NewDbAsync();
        var seed = await SeedAsync(db);
        var service = NewService(db);
        var reader = await AddUserAsync(db, "reader", admin: false);
        db.LibraryGrants.Add(new LibraryGrantEntity { UserId = reader, LibraryId = seed.LibraryId, GrantedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        await service.SetAsync(seed.AdminId, seed.Archive.Id, seed.Archive.PublicId, 1, [1]);
        // A different user edits the SAME shared layout.
        var second = await service.SetAsync(reader, seed.Archive.Id, seed.Archive.PublicId, 1, [3, 4]);

        Assert.Equal(SpreadLayoutService.SetStatus.Ok, second.Status);
        Assert.Equal(1, await db.ArchiveSpreadLayouts.CountAsync());
        Assert.Equal([3, 4], await service.GetCurrentAsync(seed.Archive.Id, 1, 10));
    }

    [Fact]
    public async Task ChangedFile_StaleLayoutIsIgnored_AndLazilyDeleted()
    {
        await using var db = await NewDbAsync();
        var seed = await SeedAsync(db);
        var service = NewService(db);
        await service.SetAsync(seed.AdminId, seed.Archive.Id, seed.Archive.PublicId, 1, [2]);

        // The scanner bumps ContentVersion when size/mtime change.
        var item = await db.ArchiveItems.SingleAsync(a => a.NodeId == seed.Archive.Id);
        item.ContentVersion = 2;
        await db.SaveChangesAsync();

        Assert.Null(await service.GetCurrentAsync(seed.Archive.Id, 2, 10));
        Assert.False(await db.ArchiveSpreadLayouts.AnyAsync());
    }

    [Fact]
    public async Task Write_AgainstAnOldContentVersion_IsRejectedAsStale()
    {
        await using var db = await NewDbAsync();
        var seed = await SeedAsync(db);

        var result = await NewService(db).SetAsync(seed.AdminId, seed.Archive.Id, seed.Archive.PublicId, 0, [1]);

        Assert.Equal(SpreadLayoutService.SetStatus.StaleContent, result.Status);
        Assert.False(await db.ArchiveSpreadLayouts.AnyAsync());
    }

    [Fact]
    public async Task Write_OnAnArchiveNotYetAnalyzed_IsRejectedAsStale()
    {
        await using var db = await NewDbAsync();
        var seed = await SeedAsync(db, analysisState: 1);

        var result = await NewService(db).SetAsync(seed.AdminId, seed.Archive.Id, seed.Archive.PublicId, 1, [1]);

        Assert.Equal(SpreadLayoutService.SetStatus.StaleContent, result.Status);
    }

    [Fact]
    public async Task Write_OnAFolder_IsNotReadable()
    {
        await using var db = await NewDbAsync();
        var seed = await SeedAsync(db);
        var folder = await AddNodeAsync(db, seed.LibraryId, CatalogNodeKind.Folder, "Series");

        var result = await NewService(db).SetAsync(seed.AdminId, folder.Id, folder.PublicId, 1, []);

        Assert.Equal(SpreadLayoutService.SetStatus.NotReadable, result.Status);
    }

    [Fact]
    public async Task Authorization_GrantedReaderMayEdit_UngrantedAndInactiveGetNotFound()
    {
        await using var db = await NewDbAsync();
        var seed = await SeedAsync(db);
        var service = NewService(db);
        var granted = await AddUserAsync(db, "granted", admin: false);
        var ungranted = await AddUserAsync(db, "ungranted", admin: false);
        var inactive = await AddUserAsync(db, "inactive", admin: false, active: false);
        db.LibraryGrants.AddRange(
            new LibraryGrantEntity { UserId = granted, LibraryId = seed.LibraryId, GrantedAt = DateTimeOffset.UtcNow },
            new LibraryGrantEntity { UserId = inactive, LibraryId = seed.LibraryId, GrantedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        Assert.Equal(SpreadLayoutService.SetStatus.Ok,
            (await service.SetAsync(granted, seed.Archive.Id, seed.Archive.PublicId, 1, [1])).Status);
        Assert.Equal(SpreadLayoutService.SetStatus.NotFound,
            (await service.SetAsync(ungranted, seed.Archive.Id, seed.Archive.PublicId, 1, [2])).Status);
        Assert.Equal(SpreadLayoutService.SetStatus.NotFound,
            (await service.SetAsync(inactive, seed.Archive.Id, seed.Archive.PublicId, 1, [2])).Status);

        Assert.Equal([1], await service.GetCurrentAsync(seed.Archive.Id, 1, 10));
    }

    [Fact]
    public async Task DeletingTheArchiveNode_CascadesToItsLayout()
    {
        await using var db = await NewDbAsync();
        var seed = await SeedAsync(db);
        await NewService(db).SetAsync(seed.AdminId, seed.Archive.Id, seed.Archive.PublicId, 1, [1]);
        Assert.True(await db.ArchiveSpreadLayouts.AnyAsync());

        await db.CatalogNodes.Where(n => n.Id == seed.Archive.Id).ExecuteDeleteAsync();

        Assert.False(await db.ArchiveSpreadLayouts.AnyAsync());
    }

    [Fact]
    public async Task InvalidStarts_AreRejected_AndNothingIsSaved()
    {
        await using var db = await NewDbAsync();
        var seed = await SeedAsync(db);

        var result = await NewService(db).SetAsync(seed.AdminId, seed.Archive.Id, seed.Archive.PublicId, 1, [4, 2]);

        Assert.Equal(SpreadLayoutService.SetStatus.Invalid, result.Status);
        Assert.False(await db.ArchiveSpreadLayouts.AnyAsync());
    }

    public static TheoryData<int[]?, bool> ValidateCases() => new()
    {
        { [], true },
        { [1], true },
        { [1, 2, 3, 9], true },
        { [9], true },                        // the last page may start a spread
        { null, false },
        { [0], false },                       // page 0 always starts; not a valid forced start
        { [10], false },                      // == pageCount
        { [-1], false },
        { [3, 3], false },                    // duplicate
        { [5, 2], false },                    // unsorted
        { [1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9], false }, // more entries than pages
    };

    [Theory]
    [MemberData(nameof(ValidateCases))]
    public void Validate_EnforcesUniqueSortedInRangeAndBoundedCount(int[]? starts, bool valid)
    {
        Assert.Equal(valid, SpreadLayoutService.Validate(starts, pageCount: 10) is null);
    }
}
