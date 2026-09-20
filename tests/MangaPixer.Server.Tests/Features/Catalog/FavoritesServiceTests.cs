namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for the per-user Favorites feature (1.21.0). Uses real
/// file-backed SQLite so FK cascade and the unique index behave exactly as in
/// production. Covers persistence + idempotency, the unique index, recently-favorited
/// ordering, FK cascade on node delete, the IsFavorite browse projection, and incognito
/// visibility (a favorite in a Private library is shown in a NORMAL session and hidden
/// while Incognito is active — matching browse/search).
/// </summary>
public sealed class FavoritesServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public FavoritesServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-fav-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        var connectionString = DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "fav.db"));
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

    private static async Task<long> AddUserAsync(MangaPixerDbContext db, string name, bool admin = true)
    {
        var user = new UserEntity
        {
            PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            IsActive = true,
            IsAdmin = admin,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<long> AddLibraryAsync(MangaPixerDbContext db, string name)
    {
        var lib = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
            DisplayName = name,
            RootPath = "/private/" + name,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        return lib.Id;
    }

    private static async Task<CatalogNodeEntity> AddNodeAsync(
        MangaPixerDbContext db, long libraryId, CatalogNodeKind kind, string name, string sortKey)
    {
        var node = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
            LibraryId = libraryId,
            ParentId = null,
            Kind = (int)kind,
            DisplayName = name,
            RelativePath = "/private/" + name,
            PathKey = "/private/" + name.ToLowerInvariant(),
            SortKey = sortKey,
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node;
    }

    private static FavoritesService NewFavoritesService(MangaPixerDbContext db)
        => new(db, new LibraryAuthorizationService(db));

    private static CatalogBrowseService NewBrowseService(MangaPixerDbContext db)
        => new(db, new LibraryAuthorizationService(db));

    [Fact]
    public async Task AddFavorite_Persists_AndIsIdempotent()
    {
        await using var db = await NewDbAsync();
        var userId = await AddUserAsync(db, "admin");
        var libId = await AddLibraryAsync(db, "Lib");
        var node = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Arc", "1A");
        var svc = NewFavoritesService(db);

        var r1 = await svc.AddFavoriteAsync(userId, node.PublicId);
        var r2 = await svc.AddFavoriteAsync(userId, node.PublicId);

        Assert.Equal(FavoritesService.FavoriteResult.Ok, r1);
        Assert.Equal(FavoritesService.FavoriteResult.Ok, r2);
        // Idempotent: exactly one row despite two adds.
        Assert.Equal(1, await db.Favorites.CountAsync(f => f.UserId == userId && f.CatalogNodeId == node.Id));
    }

    [Fact]
    public async Task AddFavorite_UnknownOrInaccessibleNode_ReturnsNodeNotFound()
    {
        await using var db = await NewDbAsync();
        var userId = await AddUserAsync(db, "admin");
        var svc = NewFavoritesService(db);

        // Unknown public id.
        Assert.Equal(FavoritesService.FavoriteResult.NodeNotFound,
            await svc.AddFavoriteAsync(userId, "does-not-exist"));

        // A reader with no grant cannot favorite (existence not leaked).
        var readerId = await AddUserAsync(db, "reader", admin: false);
        var libId = await AddLibraryAsync(db, "Lib");
        var node = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Arc", "1A");
        Assert.Equal(FavoritesService.FavoriteResult.NodeNotFound,
            await svc.AddFavoriteAsync(readerId, node.PublicId));
        Assert.Equal(0, await db.Favorites.CountAsync());
    }

    [Fact]
    public async Task UniqueIndex_RejectsDuplicateRow()
    {
        await using var db = await NewDbAsync();
        var userId = await AddUserAsync(db, "admin");
        var libId = await AddLibraryAsync(db, "Lib");
        var node = await AddNodeAsync(db, libId, CatalogNodeKind.Folder, "F", "0F");

        db.Favorites.Add(new FavoriteEntity { UserId = userId, CatalogNodeId = node.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        db.Favorites.Add(new FavoriteEntity { UserId = userId, CatalogNodeId = node.Id, CreatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task RemoveFavorite_IsIdempotent()
    {
        await using var db = await NewDbAsync();
        var userId = await AddUserAsync(db, "admin");
        var libId = await AddLibraryAsync(db, "Lib");
        var node = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Arc", "1A");
        var svc = NewFavoritesService(db);

        await svc.AddFavoriteAsync(userId, node.PublicId);
        var r1 = await svc.RemoveFavoriteAsync(userId, node.PublicId);
        var r2 = await svc.RemoveFavoriteAsync(userId, node.PublicId); // already gone

        Assert.Equal(FavoritesService.FavoriteResult.Ok, r1);
        Assert.Equal(FavoritesService.FavoriteResult.Ok, r2);
        Assert.Equal(0, await db.Favorites.CountAsync());
    }

    [Fact]
    public async Task ListFavorites_OrdersByRecentlyFavorited_NewestFirst()
    {
        await using var db = await NewDbAsync();
        var userId = await AddUserAsync(db, "admin");
        var libId = await AddLibraryAsync(db, "Lib");
        var a = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Alpha", "1A");
        var b = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Beta", "1B");
        var c = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Gamma", "1C");

        // Favorite A, then B, then C with increasing timestamps.
        var t0 = DateTimeOffset.UtcNow;
        db.Favorites.Add(new FavoriteEntity { UserId = userId, CatalogNodeId = a.Id, CreatedAt = t0 });
        db.Favorites.Add(new FavoriteEntity { UserId = userId, CatalogNodeId = b.Id, CreatedAt = t0.AddMinutes(1) });
        db.Favorites.Add(new FavoriteEntity { UserId = userId, CatalogNodeId = c.Id, CreatedAt = t0.AddMinutes(2) });
        await db.SaveChangesAsync();

        var page = await NewBrowseService(db).ListFavoritesAsync(userId);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(new[] { c.PublicId, b.PublicId, a.PublicId }, page.Items.Select(i => i.Id).ToArray());
        Assert.All(page.Items, i => Assert.True(i.IsFavorite));
    }

    [Fact]
    public async Task ListFavorites_KeysetPaging_WalksAllRows()
    {
        await using var db = await NewDbAsync();
        var userId = await AddUserAsync(db, "admin");
        var libId = await AddLibraryAsync(db, "Lib");
        var t0 = DateTimeOffset.UtcNow;
        var expected = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var node = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, $"Arc{i}", $"1A{i}");
            db.Favorites.Add(new FavoriteEntity { UserId = userId, CatalogNodeId = node.Id, CreatedAt = t0.AddMinutes(i) });
            await db.SaveChangesAsync();
            expected.Insert(0, node.PublicId); // newest-first
        }

        var svc = NewBrowseService(db);
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await svc.ListFavoritesAsync(userId, cursor, pageSize: 2);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.HasMore ? page.NextCursor : null;
        } while (cursor is not null);

        Assert.Equal(expected, seen);
    }

    [Fact]
    public async Task FkCascade_NodeDelete_RemovesFavorite()
    {
        await using var db = await NewDbAsync();
        var userId = await AddUserAsync(db, "admin");
        var libId = await AddLibraryAsync(db, "Lib");
        var node = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Arc", "1A");
        db.Favorites.Add(new FavoriteEntity { UserId = userId, CatalogNodeId = node.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.Favorites.CountAsync());

        db.CatalogNodes.Remove(node);
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.Favorites.CountAsync());
    }

    [Fact]
    public async Task Browse_ProjectsIsFavorite_ForFavoritedNodesOnly()
    {
        await using var db = await NewDbAsync();
        var userId = await AddUserAsync(db, "admin");
        var libId = await AddLibraryAsync(db, "Lib");
        var fav = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Fav", "1A");
        var plain = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Plain", "1B");
        await NewFavoritesService(db).AddFavoriteAsync(userId, fav.PublicId);

        var page = await NewBrowseService(db).BrowseAsync(userId, libId, parentId: null, cursor: null);

        var favDto = page.Items.Single(i => i.Id == fav.PublicId);
        var plainDto = page.Items.Single(i => i.Id == plain.PublicId);
        Assert.True(favDto.IsFavorite);
        Assert.False(plainDto.IsFavorite);
    }

    [Fact]
    public async Task ListFavorites_IncognitoHidesPrivateLibrary_ShownInNormalSession()
    {
        await using var db = await NewDbAsync();
        var userId = await AddUserAsync(db, "admin");
        var publicLib = await AddLibraryAsync(db, "Public");
        var privateLib = await AddLibraryAsync(db, "Private");
        var pubNode = await AddNodeAsync(db, publicLib, CatalogNodeKind.Archive, "PubArc", "1A");
        var privNode = await AddNodeAsync(db, privateLib, CatalogNodeKind.Archive, "PrivArc", "1B");

        var svc = NewFavoritesService(db);
        await svc.AddFavoriteAsync(userId, pubNode.PublicId);
        await svc.AddFavoriteAsync(userId, privNode.PublicId);

        // Mark the private library Private for this user.
        db.PrivateLibraries.Add(new PrivateLibraryEntity
        {
            UserId = userId,
            LibraryId = privateLib,
            MarkedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var browse = NewBrowseService(db);

        // Normal session: both favorites visible.
        var normal = await browse.ListFavoritesAsync(userId, incognito: false);
        Assert.Equal(2, normal.TotalCount);
        Assert.Contains(normal.Items, i => i.Id == privNode.PublicId);

        // Incognito session: the private-library favorite is hidden.
        var incognito = await browse.ListFavoritesAsync(userId, incognito: true);
        Assert.Equal(1, incognito.TotalCount);
        Assert.DoesNotContain(incognito.Items, i => i.Id == privNode.PublicId);
        Assert.Contains(incognito.Items, i => i.Id == pubNode.PublicId);
    }
}
