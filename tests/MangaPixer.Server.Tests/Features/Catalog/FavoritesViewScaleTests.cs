namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using System.Diagnostics;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// 1.50.0 lane F: the Favorites view over a large list - 2 000 favorites (400 starred folders, each also holding a stack of four starred
/// archives: 800 entries), on real file-backed SQLite. Every sort and a read-state filter walk the whole list in pages of 50 with each
/// entry exactly once, and the per-request time of a first page is written to the test output (the lane note records it; nothing is
/// asserted on time - the box is shared).
/// </summary>
public sealed class FavoritesViewScaleTests : IDisposable
{
    private const int Folders = 400;
    private const int ArchivesPerFolder = 5;
    private const int StarredPerFolder = 4;

    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;
    private readonly ITestOutputHelper _output;

    public FavoritesViewScaleTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-favview-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "favview.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext Db, long UserId)> SeedAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var user = new UserEntity
        {
            PublicId = "favscaleuser",
            UserName = "favscale",
            NormalizedUserName = "FAVSCALE",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var lib = new LibraryEntity { PublicId = "favscalelib", DisplayName = "Scale", RootPath = "/tmp/favscale", CreatedAt = DateTimeOffset.UtcNow };
        db.Users.Add(user);
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var folders = new List<CatalogNodeEntity>();
        for (var f = 0; f < Folders; f++)
        {
            var name = "Series " + f.ToString("D4", CultureInfo.InvariantCulture);
            folders.Add(new CatalogNodeEntity
            {
                PublicId = "fs" + f.ToString(CultureInfo.InvariantCulture),
                LibraryId = lib.Id,
                Kind = (int)CatalogNodeKind.Folder,
                DisplayName = name,
                RelativePath = name,
                PathKey = name.ToLowerInvariant(),
                SortKey = SortKey.ForNode(CatalogNodeKind.Folder, name),
                CreatedAt = t0.AddHours(f),
                LatestDescendantAddedAt = t0.AddHours(f % 7 == 0 ? f : f + 1000),
            });
        }
        db.CatalogNodes.AddRange(folders);
        await db.SaveChangesAsync();

        var archives = new List<CatalogNodeEntity>();
        foreach (var (folder, f) in folders.Select((x, i) => (x, i)))
        {
            for (var a = 0; a < ArchivesPerFolder; a++)
            {
                var name = folder.DisplayName + " v" + a.ToString(CultureInfo.InvariantCulture);
                archives.Add(new CatalogNodeEntity
                {
                    PublicId = folder.PublicId + "a" + a.ToString(CultureInfo.InvariantCulture),
                    LibraryId = lib.Id,
                    ParentId = folder.Id,
                    Kind = (int)CatalogNodeKind.Archive,
                    DisplayName = name,
                    RelativePath = name,
                    PathKey = name.ToLowerInvariant(),
                    SortKey = SortKey.ForNode(CatalogNodeKind.Archive, name),
                    CreatedAt = t0.AddHours(f).AddMinutes(a),
                });
            }
        }
        db.CatalogNodes.AddRange(archives);
        await db.SaveChangesAsync();

        var minute = 0;
        foreach (var folder in folders)
            db.Favorites.Add(new FavoriteEntity { UserId = user.Id, CatalogNodeId = folder.Id, CreatedAt = t0.AddMinutes(minute++) });
        foreach (var archive in archives.Where((_, i) => i % ArchivesPerFolder < StarredPerFolder))
            db.Favorites.Add(new FavoriteEntity { UserId = user.Id, CatalogNodeId = archive.Id, CreatedAt = t0.AddMinutes(minute++) });
        // Some reading state, so the read-state filter has something to judge: every third folder's first archive is read.
        foreach (var archive in archives.Where((_, i) => i % (ArchivesPerFolder * 3) == 0))
            db.ReadMarks.Add(new ReadMarkEntity { UserId = user.Id, ItemId = archive.Id, MarkedAt = t0 });
        await db.SaveChangesAsync();
        Assert.Equal(Folders * (1 + StarredPerFolder), await db.Favorites.CountAsync());
        return (db, user.Id);
    }

    [Fact]
    public async Task TwoThousandFavorites_EverySortWalksEachEntryOnce_AndTheFirstPageCostIsRecorded()
    {
        var (db, userId) = await SeedAsync();
        await using var _ = db;
        var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

        var queries = new (string Label, FavoritesQuery Query)[]
        {
            ("favorited", FavoritesQuery.Default),
            ("name", new FavoritesQuery("name")),
            ("name desc", new FavoritesQuery("name", SortDirection.Descending)),
            ("recentlyAdded", new FavoritesQuery("recentlyAdded")),
            ("recentlyUpdated", new FavoritesQuery("recentlyUpdated")),
            ("name + unread", new FavoritesQuery("name", ReadState: BrowseReadStateFilter.Unread)),
            ("name + reading", new FavoritesQuery("name", ReadState: BrowseReadStateFilter.Reading)),
            ("name + archives", new FavoritesQuery("name", Kind: FavoriteKindFilter.Archives)),
        };
        foreach (var (label, query) in queries)
        {
            // Warm once (the first query of a connection pays the statement compile), then time five first pages.
            await service.ListFavoritesAsync(userId, query, null, 50, false, CancellationToken.None);
            var times = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                await service.ListFavoritesAsync(userId, query, null, 50, false, CancellationToken.None);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            var total = -1;
            var pages = 0;
            do
            {
                var page = await service.ListFavoritesAsync(userId, query, cursor, 50, false, CancellationToken.None);
                total = page.TotalCount;
                foreach (var n in page.Items)
                    Assert.True(seen.Add(n.FavoriteStackCount is null ? n.Id : "stack:" + n.Id), $"{label}: {n.Id} listed twice");
                cursor = page.HasMore ? page.NextCursor : null;
                pages++;
            }
            while (cursor is not null && pages < 100);

            Assert.Equal(total, seen.Count);
            if (query.ReadState == BrowseReadStateFilter.All && query.Kind == FavoriteKindFilter.All)
                Assert.Equal(Folders * 2, total);
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"FAVSCALE {label}: entries {total}, first page median {times[2]:F1} ms, max {times[4]:F1} ms, pages {pages}"));
        }
    }
}
