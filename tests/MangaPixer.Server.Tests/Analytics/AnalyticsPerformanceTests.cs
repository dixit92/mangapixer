namespace com.lifepixer.mangapixer.Tests.Server.Analytics;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Analytics;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Performance note (1.22.0 lane E) for the on-demand analytics aggregation:
/// the integrator's brief accepted on-demand aggregation (no rollup/snapshot
/// table) on the condition that query time on a realistic synthetic dataset
/// is measured and reported, with a rollup proposed only if it turns out
/// slow. This seeds a dataset well beyond what a real single-instance
/// MangaPixer deployment is likely to reach (50 libraries, 200 users, 20,000
/// archive nodes, 100,000 reading-progress rows, 20,000 bookmarks, 10,000
/// favorites, 100 Private-library markings) and measures both endpoints'
/// service-layer latency. See the feature note's Dashboard v1 section for the
/// measured numbers and the "no rollup needed" conclusion.
/// </summary>
public sealed class AnalyticsPerformanceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;
    private readonly ITestOutputHelper _output;

    public AnalyticsPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-analytics-perf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        var dbPath = Path.Combine(_tempDir, "test.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(dbPath);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private const int LibraryCount = 50;
    private const int UserCount = 200;
    private const int NodesPerLibrary = 400; // 20,000 archive nodes total
    private const int ProgressRowCount = 100_000;
    private const int BookmarkRowCount = 20_000;
    private const int FavoriteRowCount = 10_000;
    private const int PrivateMarkCount = 100;

    [Fact]
    public async Task Overview_And_UserAnalytics_CompleteWithinBudget_OnRealisticSyntheticDataset()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        await SeedAsync(db);

        var service = new AnalyticsService(db, new DiagnosticsService(db));

        var overviewSw = Stopwatch.StartNew();
        var overview = await service.GetOverviewAsync();
        overviewSw.Stop();

        var usersSw = Stopwatch.StartNew();
        var rows = await service.GetUserAnalyticsAsync();
        usersSw.Stop();

        _output.WriteLine($"GetOverviewAsync: {overviewSw.ElapsedMilliseconds} ms");
        _output.WriteLine($"GetUserAnalyticsAsync: {usersSw.ElapsedMilliseconds} ms ({rows.Count} rows)");

        Assert.Equal(UserCount, rows.Count);
        Assert.Equal(LibraryCount, overview.LibraryCount);

        // Generous budget — this is a regression guard against an accidental
        // per-row query, not a tight performance SLA. Both endpoints measured
        // comfortably under 1s on this dataset in the reference container run
        // (see the feature note for the exact numbers); 5s leaves headroom
        // for slower CI hosts without masking an actual N+1 regression.
        Assert.True(overviewSw.ElapsedMilliseconds < 5000,
            $"GetOverviewAsync took {overviewSw.ElapsedMilliseconds} ms, expected < 5000 ms");
        Assert.True(usersSw.ElapsedMilliseconds < 5000,
            $"GetUserAnalyticsAsync took {usersSw.ElapsedMilliseconds} ms, expected < 5000 ms");

        await db.DisposeAsync();
    }

    private static async Task SeedAsync(MangaPixerDbContext db)
    {
        var random = new Random(42);

        for (var i = 1; i <= LibraryCount; i++)
        {
            db.Libraries.Add(new LibraryEntity
            {
                Id = i,
                PublicId = OpaqueId.Encode(i),
                DisplayName = $"lib-{i}",
                RootPath = $"/private/lib-{i}",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        for (var i = 1; i <= UserCount; i++)
        {
            db.Users.Add(new UserEntity
            {
                Id = i,
                PublicId = OpaqueId.Encode(i),
                UserName = $"user-{i}",
                NormalizedUserName = $"USER-{i}",
                IsActive = true,
                IsAdmin = i == 1,
                PasswordHash = "hash",
                SecurityStamp = "stamp",
                CreatedAt = DateTimeOffset.UtcNow,
                LastLoginAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        var nodeId = 1L;
        var nodeIdsByLibrary = new Dictionary<long, List<long>>();
        for (var libId = 1L; libId <= LibraryCount; libId++)
        {
            var ids = new List<long>(NodesPerLibrary);
            for (var n = 0; n < NodesPerLibrary; n++)
            {
                var id = nodeId++;
                ids.Add(id);
                db.CatalogNodes.Add(new CatalogNodeEntity
                {
                    Id = id,
                    PublicId = OpaqueId.Encode(id),
                    LibraryId = libId,
                    Kind = (int)CatalogNodeKind.Archive,
                    DisplayName = $"item-{id}",
                    RelativePath = $"item-{id}.cbz",
                    PathKey = $"item-{id}.cbz",
                    SortKey = $"item-{id}",
                    Availability = 0,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            nodeIdsByLibrary[libId] = ids;
            if (libId % 5 == 0)
                await db.SaveChangesAsync();
        }
        await db.SaveChangesAsync();

        var allNodeIds = nodeIdsByLibrary.Values.SelectMany(v => v).ToList();

        // (UserId, ItemId) is unique on reading_progress — dedupe rather than
        // let a random collision throw partway through the seed.
        var progressPairs = new HashSet<(long UserId, long ItemId)>();
        while (progressPairs.Count < ProgressRowCount)
            progressPairs.Add((random.Next(1, UserCount + 1), allNodeIds[random.Next(allNodeIds.Count)]));

        var progressIndex = 0;
        foreach (var (userId, itemId) in progressPairs)
        {
            db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = userId,
                ItemId = itemId,
                State = random.Next(0, 3),
                EntryKey = "e",
                LastMutationId = $"m{progressIndex}",
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-random.Next(0, 100_000)),
            });
            if (++progressIndex % 2000 == 0)
                await db.SaveChangesAsync();
        }
        await db.SaveChangesAsync();

        for (var i = 0; i < BookmarkRowCount; i++)
        {
            var userId = random.Next(1, UserCount + 1);
            var itemId = allNodeIds[random.Next(allNodeIds.Count)];
            db.Bookmarks.Add(new BookmarkEntity
            {
                UserId = userId,
                ItemId = itemId,
                EntryKey = "e",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            if (i % 2000 == 0)
                await db.SaveChangesAsync();
        }
        await db.SaveChangesAsync();

        // (UserId, CatalogNodeId) is unique on favorites — dedupe.
        var favoritePairs = new HashSet<(long UserId, long NodeId)>();
        while (favoritePairs.Count < FavoriteRowCount)
            favoritePairs.Add((random.Next(1, UserCount + 1), allNodeIds[random.Next(allNodeIds.Count)]));

        var favoriteIndex = 0;
        foreach (var (userId, nodeIdFav) in favoritePairs)
        {
            db.Favorites.Add(new FavoriteEntity
            {
                UserId = userId,
                CatalogNodeId = nodeIdFav,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            if (++favoriteIndex % 2000 == 0)
                await db.SaveChangesAsync();
        }
        await db.SaveChangesAsync();

        // (UserId, LibraryId) is unique — dedupe rather than let a random
        // collision throw partway through the seed.
        var privatePairs = new HashSet<(long UserId, long LibraryId)>();
        while (privatePairs.Count < PrivateMarkCount)
            privatePairs.Add((random.Next(1, UserCount + 1), random.Next(1, LibraryCount + 1)));

        foreach (var (userId, libraryId) in privatePairs)
        {
            db.PrivateLibraries.Add(new PrivateLibraryEntity
            {
                UserId = userId,
                LibraryId = libraryId,
                MarkedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }
}
