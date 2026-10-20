using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Home;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Server.Features.Home;

/// <summary>
/// Service-with-DB tests for the 1.46.0 lane D change to the home "New chapters" row: the stacks are capped BEFORE their read rollups
/// are resolved (the rollups only for the stacks the row can show), and the answer is the same as before.
/// <list type="bullet">
/// <item><description>Equivalence: on one seeded library (folder stacks, loose archives, tied times, read / reading / unread rollups,
/// archives outside the window), every (read-state filter, cap) answer equals the full, uncapped answer filtered and cut to the cap - the
/// rule the 1.17.0 code implemented by resolving every stack first.</description></item>
/// <item><description>Cost: with no filter, the rollup query names only the capped stacks (red on the 1.45 code, which named every
/// stack); with a filter it stops once the cap is filled.</description></item>
/// </list>
/// </summary>
public sealed class RecentChaptersCapTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly string _tempDir;
    private readonly RecordingSqliteConnection _connection;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public RecentChaptersCapTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-recentcap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _connection = new RecordingSqliteConnection(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "recent.db")));
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    /// <summary>Stacks the seed builds in the library (40 shown at most by a cap of 50, so the "full" answer holds every stack).</summary>
    private const int SeededStacks = 40;

    [Fact]
    public async Task EveryFilterAndCap_EqualsTheFullAnswer_FilteredThenCut()
    {
        await using var db = new MangaPixerDbContext(_options);
        var userId = await SeedAsync(db);
        var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));

        var full = Stacks(await service.GetRecentChaptersAsync(userId, perLibrary: RecentChaptersService.MaxPerLibrary));
        Assert.Equal(SeededStacks, full.Count);
        // The seed covers all three states, tied times and loose archives, so the filters and the tie-break are exercised.
        Assert.Equal(new[] { "read", "reading", "unread" }, full.Select(s => s.ReadState).Distinct().Order().ToArray());
        Assert.Contains(full, s => !s.IsFolder);
        Assert.True(full.GroupBy(s => s.LatestAddedAt).Any(g => g.Count() > 1), "the seed has stacks with the same newest time");

        foreach (var filter in Enum.GetValues<HomeReadStateFilter>())
        {
            var matching = full.Where(s => Matches(filter, s.ReadState)).ToList();
            foreach (var cap in new[] { 1, 2, 3, 5, 7, 12, 16, 17, 33, 40, 50 })
            {
                var actual = Stacks(await service.GetRecentChaptersAsync(userId, perLibrary: cap, readState: filter));
                var expected = matching.Take(cap).ToList();
                Assert.True(Json(expected) == Json(actual), $"filter {filter}, cap {cap}: {actual.Count} stacks vs {expected.Count} expected");
            }
        }
    }

    [Fact]
    public async Task WithoutAFilter_TheRollupQuery_NamesOnlyTheCappedStacks()
    {
        await using var db = new MangaPixerDbContext(_options);
        var userId = await SeedAsync(db);
        var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));

        _connection.Commands.Clear();
        var result = Stacks(await service.GetRecentChaptersAsync(userId, perLibrary: 12));

        Assert.Equal(12, result.Count);
        var rollupQueries = RollupTargetCounts();
        Assert.Equal(new[] { 12 }, rollupQueries);
    }

    [Fact]
    public async Task WithAFilter_TheRollupQueries_StopOnceTheCapIsFilled()
    {
        await using var db = new MangaPixerDbContext(_options);
        var userId = await SeedAsync(db);
        var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));

        _connection.Commands.Clear();
        var unread = Stacks(await service.GetRecentChaptersAsync(userId, perLibrary: 2, readState: HomeReadStateFilter.Unread));

        Assert.Equal(2, unread.Count);
        // One chunk of at least FilteredRollupChunk stacks holds two unread stacks in this seed - never all 40.
        var rollupQueries = RollupTargetCounts();
        Assert.Equal(new[] { RecentChaptersService.FilteredRollupChunk }, rollupQueries);
    }

    private static bool Matches(HomeReadStateFilter filter, string readState) => filter switch
    {
        HomeReadStateFilter.Read => readState == "read",
        HomeReadStateFilter.Reading => readState == "reading",
        HomeReadStateFilter.Unread => readState == "unread",
        _ => true,
    };

    private static List<RecentChapterStack> Stacks(RecentChaptersDto dto) => dto.Libraries.Single().Stacks.ToList();

    private static string Json(IEnumerable<RecentChapterStack> stacks) => JsonSerializer.Serialize(stacks);

    /// <summary>For every read-rollup query run since the last clear, how many stack ids its IN list names.</summary>
    private List<int> RollupTargetCounts() => _connection.Commands
        .Select(c => c.CommandText)
        .Where(t => t.Contains("WITH RECURSIVE subtree", StringComparison.Ordinal))
        .Select(t => Regex.Match(t, @"WHERE r\.Id IN \(([0-9,]+)\)").Groups[1].Value.Split(',').Length)
        .ToList();

    /// <summary>
    /// One library, one admin. 40 stacks: 28 series folders (some through a unit folder "Vol 1", some with an older archive outside the
    /// window) and 12 loose archives in the library root. Newest times repeat every 7 stacks (ties broken by the stack id). Read state by
    /// stack index: every third read, every fifth (not read) reading, the rest unread.
    /// </summary>
    private static async Task<long> SeedAsync(MangaPixerDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var lib = new LibraryEntity { PublicId = "capLib", DisplayName = "Cap Library", RootPath = "/private/cap", CreatedAt = Now };
        db.Libraries.Add(lib);
        var user = new UserEntity
        {
            PublicId = "capUser",
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = Now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        for (var i = 0; i < SeededStacks; i++)
        {
            var newest = Now.AddMinutes(-10 * (i % 7) - (i / 7));
            if (i % 7 == 3)
                newest = Now.AddMinutes(-31); // a tie with other stacks of the same residue class
            var archives = new List<CatalogNodeEntity>();
            if (i % 10 < 3)
            {
                archives.Add(Node(lib.Id, null, $"loose{i:D2}", CatalogNodeKind.Archive, newest));
                db.CatalogNodes.AddRange(archives);
                await db.SaveChangesAsync();
            }
            else
            {
                var series = Node(lib.Id, null, $"series{i:D2}", CatalogNodeKind.Folder, Now.AddDays(-90));
                db.CatalogNodes.Add(series);
                await db.SaveChangesAsync();
                var holder = series;
                if (i % 4 == 0)
                {
                    holder = Node(lib.Id, series.Id, $"Vol 1 {i:D2}", CatalogNodeKind.Folder, Now.AddDays(-90), displayName: "Vol 1");
                    db.CatalogNodes.Add(holder);
                    await db.SaveChangesAsync();
                }
                archives.Add(Node(lib.Id, holder.Id, $"s{i:D2}a", CatalogNodeKind.Archive, newest));
                archives.Add(Node(lib.Id, holder.Id, $"s{i:D2}b", CatalogNodeKind.Archive, newest.AddHours(-5)));
                if (i % 3 == 1)
                    archives.Add(Node(lib.Id, holder.Id, $"s{i:D2}old", CatalogNodeKind.Archive, Now.AddDays(-200)));
                db.CatalogNodes.AddRange(archives);
                await db.SaveChangesAsync();
            }

            if (i % 3 == 0)
            {
                foreach (var a in archives)
                    db.ReadMarks.Add(new ReadMarkEntity { UserId = user.Id, ItemId = a.Id, MarkedAt = Now });
            }
            else if (i % 5 == 0)
            {
                db.ReadingProgress.Add(new ReadingProgressEntity
                {
                    UserId = user.Id,
                    ItemId = archives[0].Id,
                    State = (int)ReadingState.InProgress,
                    EntryKey = "e1",
                    LastMutationId = "m" + i,
                    UpdatedAt = Now,
                });
            }
        }
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static CatalogNodeEntity Node(long libraryId, long? parentId, string publicId, CatalogNodeKind kind, DateTimeOffset createdAt,
        string? displayName = null) => new()
        {
            PublicId = publicId.Replace(" ", "", StringComparison.Ordinal),
            LibraryId = libraryId,
            ParentId = parentId,
            Kind = (int)kind,
            DisplayName = displayName ?? publicId,
            RelativePath = "/private/" + publicId,
            PathKey = "/private/" + publicId.ToLowerInvariant(),
            SortKey = (kind == CatalogNodeKind.Folder ? "0" : "1") + publicId,
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = createdAt,
        };

    /// <summary>A SQLite connection that keeps every command created on it, so a test can read the SQL a service ran.</summary>
    private sealed class RecordingSqliteConnection(string connectionString) : SqliteConnection(connectionString)
    {
        public List<DbCommand> Commands { get; } = [];

        protected override DbCommand CreateDbCommand()
        {
            var command = base.CreateDbCommand();
            Commands.Add(command);
            return command;
        }
    }
}
