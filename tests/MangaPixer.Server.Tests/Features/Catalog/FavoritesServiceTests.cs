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
        MangaPixerDbContext db, long libraryId, CatalogNodeKind kind, string name, string sortKey, long? parentId = null)
    {
        var node = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
            LibraryId = libraryId,
            ParentId = parentId,
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

    // --- Stacking (1.27.0) ---

    private static async Task StarAtAsync(MangaPixerDbContext db, long userId, CatalogNodeEntity node, DateTimeOffset at)
    {
        db.Favorites.Add(new FavoriteEntity { UserId = userId, CatalogNodeId = node.Id, CreatedAt = at });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ListFavorites_ArchivesSharingAFolder_StackAtTheirNewestFavorite()
    {
        await using var db = await NewDbAsync();
        var userId = await AddUserAsync(db, "admin");
        var libId = await AddLibraryAsync(db, "Lib");
        var folder = await AddNodeAsync(db, libId, CatalogNodeKind.Folder, "Berserk", "0Berserk");
        var v1 = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Berserk v01", "1Berserk v01", folder.Id);
        var v2 = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Berserk v02", "1Berserk v02", folder.Id);
        var loose = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Akira", "1Akira");

        var t0 = DateTimeOffset.UtcNow;
        await StarAtAsync(db, userId, v1, t0);
        await StarAtAsync(db, userId, loose, t0.AddMinutes(1));
        await StarAtAsync(db, userId, v2, t0.AddMinutes(2));

        var page = await NewBrowseService(db).ListFavoritesAsync(userId);

        // The stack sits at v2's place (newest), ahead of the loose archive; v1 is not repeated.
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { folder.PublicId, loose.PublicId }, page.Items.Select(i => i.Id).ToArray());
        var stack = page.Items[0];
        Assert.Equal(CatalogNodeKind.Folder, stack.Kind);
        Assert.Equal("Berserk", stack.DisplayName);
        Assert.Equal(2, stack.FavoriteStackCount);
        Assert.False(stack.IsFavorite); // the folder itself is not starred
        // Folder cover: its first archive by sort key.
        Assert.Equal($"/api/v1/items/{v1.PublicId}/cover", stack.CoverUrl);
        Assert.Null(page.Items[1].FavoriteStackCount);
        Assert.True(page.Items[1].IsFavorite);
    }

    [Fact]
    public async Task ListFavorites_OneStarredArchiveInAFolder_StaysASingleCard()
    {
        await using var db = await NewDbAsync();
        var userId = await AddUserAsync(db, "admin");
        var libId = await AddLibraryAsync(db, "Lib");
        var folder = await AddNodeAsync(db, libId, CatalogNodeKind.Folder, "Monster", "0Monster");
        var only = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Monster v01", "1Monster v01", folder.Id);
        await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Monster v02", "1Monster v02", folder.Id);
        await StarAtAsync(db, userId, only, DateTimeOffset.UtcNow);

        var page = await NewBrowseService(db).ListFavoritesAsync(userId);

        var item = Assert.Single(page.Items);
        Assert.Equal(only.PublicId, item.Id);
        Assert.Null(item.FavoriteStackCount);
    }

    [Fact]
    public async Task ListFavorites_GroupsByDirectParent_AndStarredFoldersStayCards()
    {
        await using var db = await NewDbAsync();
        var userId = await AddUserAsync(db, "admin");
        var libId = await AddLibraryAsync(db, "Lib");
        // Series > Arc > chapters. Two starred chapters in Arc, one starred volume directly in
        // Series: the stack is Arc (direct parent), never the top-level Series.
        var series = await AddNodeAsync(db, libId, CatalogNodeKind.Folder, "One Piece", "0One Piece");
        var arc = await AddNodeAsync(db, libId, CatalogNodeKind.Folder, "Arc 1", "0Arc 1", series.Id);
        var c1 = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Ch 1", "1Ch 1", arc.Id);
        var c2 = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Ch 2", "1Ch 2", arc.Id);
        var extra = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "Extra", "1Extra", series.Id);
        // Two starred FOLDERS under the same parent never stack.
        var other = await AddNodeAsync(db, libId, CatalogNodeKind.Folder, "Arc 2", "0Arc 2", series.Id);

        var t0 = DateTimeOffset.UtcNow;
        await StarAtAsync(db, userId, c1, t0);
        await StarAtAsync(db, userId, c2, t0.AddMinutes(1));
        await StarAtAsync(db, userId, extra, t0.AddMinutes(2));
        await StarAtAsync(db, userId, arc, t0.AddMinutes(3));
        await StarAtAsync(db, userId, other, t0.AddMinutes(4));

        var page = await NewBrowseService(db).ListFavoritesAsync(userId);

        Assert.Equal(4, page.TotalCount);
        Assert.Equal(
            new[] { (other.PublicId, (int?)null), (arc.PublicId, null), (extra.PublicId, null), (arc.PublicId, 2) },
            page.Items.Select(i => (i.Id, i.FavoriteStackCount)).ToArray());
        // The Arc stack carries the folder's own star (Arc itself is starred too).
        Assert.True(page.Items[3].IsFavorite);
        Assert.DoesNotContain(page.Items, i => i.Id == series.PublicId);
    }

    [Fact]
    public async Task ListFavorites_StackSpanningAPageBoundary_AppearsOnceAndPagingWalksEverything()
    {
        await using var db = await NewDbAsync();
        var userId = await AddUserAsync(db, "admin");
        var libId = await AddLibraryAsync(db, "Lib");
        var folder = await AddNodeAsync(db, libId, CatalogNodeKind.Folder, "Dorohedoro", "0Dorohedoro");
        var d1 = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "D1", "1D1", folder.Id);
        var d2 = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "D2", "1D2", folder.Id);
        var d3 = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "D3", "1D3", folder.Id);
        var a = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "A", "1A");
        var b = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "B", "1B");
        var c = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "C", "1C");

        // Flat newest-first this would be: c, d3, b, d2, a, d1 - the stack's members
        // interleave with singles and would land on three different pages of size 2.
        var t0 = DateTimeOffset.UtcNow;
        await StarAtAsync(db, userId, d1, t0);
        await StarAtAsync(db, userId, a, t0.AddMinutes(1));
        await StarAtAsync(db, userId, d2, t0.AddMinutes(2));
        await StarAtAsync(db, userId, b, t0.AddMinutes(3));
        await StarAtAsync(db, userId, d3, t0.AddMinutes(4));
        await StarAtAsync(db, userId, c, t0.AddMinutes(5));

        var svc = NewBrowseService(db);
        foreach (var size in new[] { 1, 2, 3 })
        {
            var seen = new List<string>();
            string? cursor = null;
            do
            {
                var page = await svc.ListFavoritesAsync(userId, cursor, pageSize: size);
                Assert.Equal(4, page.TotalCount);
                seen.AddRange(page.Items.Select(i => i.Id));
                cursor = page.HasMore ? page.NextCursor : null;
            } while (cursor is not null);

            Assert.Equal(new[] { c.PublicId, folder.PublicId, b.PublicId, a.PublicId }, seen);
        }
    }

    [Fact]
    public async Task ListFavorites_StackCountsOnlyVisibleFavorites()
    {
        await using var db = await NewDbAsync();
        var userId = await AddUserAsync(db, "admin");
        var libId = await AddLibraryAsync(db, "Lib");
        var privLib = await AddLibraryAsync(db, "Hidden");

        // Tombstoned member: the folder keeps one visible starred archive -> no stack.
        var folder = await AddNodeAsync(db, libId, CatalogNodeKind.Folder, "Vagabond", "0Vagabond");
        var live = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "V1", "1V1", folder.Id);
        var gone = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "V2", "1V2", folder.Id);
        gone.Availability = (int)CatalogNodeAvailability.Tombstoned;

        // A stack inside a library the user marks Private.
        var privFolder = await AddNodeAsync(db, privLib, CatalogNodeKind.Folder, "Hidden Series", "0Hidden");
        var p1 = await AddNodeAsync(db, privLib, CatalogNodeKind.Archive, "P1", "1P1", privFolder.Id);
        var p2 = await AddNodeAsync(db, privLib, CatalogNodeKind.Archive, "P2", "1P2", privFolder.Id);

        var t0 = DateTimeOffset.UtcNow;
        await StarAtAsync(db, userId, live, t0);
        await StarAtAsync(db, userId, gone, t0.AddMinutes(1));
        await StarAtAsync(db, userId, p1, t0.AddMinutes(2));
        await StarAtAsync(db, userId, p2, t0.AddMinutes(3));
        db.PrivateLibraries.Add(new PrivateLibraryEntity { UserId = userId, LibraryId = privLib, MarkedAt = t0 });
        await db.SaveChangesAsync();

        var browse = NewBrowseService(db);

        var normal = await browse.ListFavoritesAsync(userId, incognito: false);
        Assert.Equal(
            new[] { (privFolder.PublicId, (int?)2), (live.PublicId, null) },
            normal.Items.Select(i => (i.Id, i.FavoriteStackCount)).ToArray());

        var incognito = await browse.ListFavoritesAsync(userId, incognito: true);
        Assert.Equal(1, incognito.TotalCount);
        var only = Assert.Single(incognito.Items);
        Assert.Equal(live.PublicId, only.Id);
        Assert.Null(only.FavoriteStackCount);
    }

    [Fact]
    public async Task ListFavorites_ReaderWithoutGrant_SeesNoStack()
    {
        await using var db = await NewDbAsync();
        var readerId = await AddUserAsync(db, "reader", admin: false);
        var libId = await AddLibraryAsync(db, "Lib");
        var folder = await AddNodeAsync(db, libId, CatalogNodeKind.Folder, "Blame", "0Blame");
        var b1 = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "B1", "1B1", folder.Id);
        var b2 = await AddNodeAsync(db, libId, CatalogNodeKind.Archive, "B2", "1B2", folder.Id);
        // Rows left behind after the reader's grant was revoked.
        await StarAtAsync(db, readerId, b1, DateTimeOffset.UtcNow);
        await StarAtAsync(db, readerId, b2, DateTimeOffset.UtcNow.AddMinutes(1));

        var page = await NewBrowseService(db).ListFavoritesAsync(readerId);

        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
    }
}
