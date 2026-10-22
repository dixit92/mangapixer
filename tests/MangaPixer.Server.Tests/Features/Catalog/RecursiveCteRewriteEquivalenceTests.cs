namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using System.Data.Common;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.47.0 lane S: the recursive statements rewritten for speed give the SAME rows as their 1.46.0 text, which this class keeps verbatim
/// as the reference. A deterministic pseudo-random tree in two libraries: nested folders (to depth 5), tombstoned folders with live
/// archives below them, tombstoned archives, SortKey ties (equal names, ties by id), empty folders and folders holding only tombstoned
/// archives, with read marks and in-progress records of two viewers. Every folder is asked alone and all of them at once.
/// Real file-backed SQLite with the production connection settings; synthetic names.
/// </summary>
public sealed class RecursiveCteRewriteEquivalenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public RecursiveCteRewriteEquivalenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-cte-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "cte.db")))
            .Options;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private sealed record Seeded(MangaPixerDbContext Db, List<long> Libraries, List<(long Id, long LibraryId)> Folders, List<long> Users);

    private async Task<Seeded> SeedAsync(int seed)
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        var random = new Random(seed);
        var libraries = new List<long>();
        for (var l = 0; l < 2; l++)
        {
            var library = new LibraryEntity { PublicId = OpaqueId.Encode(10 + l), DisplayName = $"Lib {l}", RootPath = $"/synthetic/{l}", CreatedAt = DateTimeOffset.UtcNow };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            libraries.Add(library.Id);
        }
        var users = new List<long>();
        for (var u = 0; u < 2; u++)
        {
            var user = new UserEntity
            {
                PublicId = OpaqueId.Encode(100 + u),
                UserName = $"viewer{u}",
                NormalizedUserName = $"VIEWER{u}",
                IsActive = true,
                IsAdmin = true,
                PasswordHash = "hash",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            users.Add(user.Id);
        }

        var serial = 0;
        var folders = new List<(long Id, long LibraryId)>();
        var archives = new List<long>();
        // Few distinct names, so SortKeys tie often (ties are broken by id).
        string[] names = ["Alpha 001", "Alpha 002", "Beta v01", "Beta v02", "Gamma c010", "gamma c010", "Delta", "Alpha 001"];
        CatalogNodeEntity Node(long lib, long? parent, CatalogNodeKind kind, string name, bool tombstoned) => new()
        {
            PublicId = OpaqueId.Encode(1000 + ++serial),
            LibraryId = lib,
            ParentId = parent,
            Kind = (int)kind,
            DisplayName = name,
            RelativePath = $"/synthetic/n{serial}",
            PathKey = $"/synthetic/n{serial}",
            SortKey = SortKey.ForNode(kind, name),
            Availability = tombstoned ? (int)CatalogNodeAvailability.Tombstoned : (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        async Task FillAsync(long lib, long? parent, int depth)
        {
            // About 10 root nodes, then 0-4 children per folder, more folders near the top: a few dozen folders per library.
            var children = depth == 0 ? 10 : random.Next(0, 5);
            for (var c = 0; c < children; c++)
            {
                var folder = depth < 5 && random.NextDouble() < (depth < 2 ? 0.6 : 0.35);
                var node = Node(lib, parent, folder ? CatalogNodeKind.Folder : CatalogNodeKind.Archive, names[random.Next(names.Length)],
                    tombstoned: random.NextDouble() < 0.15);
                db.CatalogNodes.Add(node);
                await db.SaveChangesAsync();
                if (folder)
                {
                    folders.Add((node.Id, lib));
                    await FillAsync(lib, node.Id, depth + 1);
                }
                else
                {
                    db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = node.Id, ContentVersion = 1, PageCount = 5, ByteLength = 1 });
                    archives.Add(node.Id);
                }
            }
        }
        foreach (var lib in libraries)
            await FillAsync(lib, null, 0);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        foreach (var archive in archives)
        {
            foreach (var user in users)
            {
                var roll = random.NextDouble();
                if (roll < 0.3)
                    db.ReadMarks.Add(new ReadMarkEntity { UserId = user, ItemId = archive, MarkedAt = now.AddMinutes(-random.Next(1000)) });
                if (roll is > 0.2 and < 0.45)
                {
                    db.ReadingProgress.Add(new ReadingProgressEntity
                    {
                        UserId = user,
                        ItemId = archive,
                        ContentVersion = 1,
                        EntryKey = "entry1",
                        Ordinal = 2,
                        State = random.NextDouble() < 0.8 ? 1 : 2,
                        Revision = 1,
                        LastMutationId = Guid.NewGuid().ToString("N"),
                        UpdatedAt = now.AddMinutes(-random.Next(1000)),
                    });
                }
            }
        }
        await db.SaveChangesAsync();
        return new Seeded(db, libraries, folders, users);
    }

    // ---- The 1.46.0 statements, verbatim (the reference).

    private const string FolderCoversReference = """
        WITH RECURSIVE descendants(RootId, NodeId, Kind, SortKey, Availability) AS (
            SELECT r.Id, cn.Id, cn.Kind, cn.SortKey, cn.Availability
            FROM catalog_nodes r
            JOIN catalog_nodes cn ON cn.ParentId = r.Id
            WHERE r.Id IN ({ids})
            UNION ALL
            SELECT d.RootId, cn.Id, cn.Kind, cn.SortKey, cn.Availability
            FROM descendants d
            JOIN catalog_nodes cn ON cn.ParentId = d.NodeId
        ),
        ranked AS (
            SELECT d.RootId, cn.Id AS CoverId, cn.PublicId AS CoverPublicId,
                   ROW_NUMBER() OVER (PARTITION BY d.RootId ORDER BY d.SortKey, d.NodeId) AS rn
            FROM descendants d
            JOIN catalog_nodes cn ON d.NodeId = cn.Id
            WHERE d.Kind = 1 AND d.Availability != 5
        )
        SELECT RootId, CoverId, CoverPublicId FROM ranked WHERE rn = 1;
        """;

    private const string NextUnreadReference = """
        WITH RECURSIVE descendants(NodeId) AS (
            SELECT cn.Id FROM catalog_nodes cn
            WHERE cn.LibraryId = $lib
              AND cn.Availability != 5
              AND (($parent IS NULL AND cn.ParentId IS NULL) OR cn.ParentId = $parent)
            UNION ALL
            SELECT cn.Id FROM descendants d
            JOIN catalog_nodes cn ON cn.ParentId = d.NodeId
            WHERE cn.Availability != 5
        )
        SELECT cn.PublicId
        FROM descendants d
        JOIN catalog_nodes cn ON cn.Id = d.NodeId AND cn.Kind = 1
        LEFT JOIN catalog_nodes parent ON cn.ParentId = parent.Id
        JOIN libraries lib ON lib.Id = cn.LibraryId
        LEFT JOIN archive_items ai ON ai.NodeId = cn.Id
        LEFT JOIN read_marks rm ON rm.ItemId = cn.Id AND rm.UserId = $user
        LEFT JOIN reading_progress rp ON rp.ItemId = cn.Id AND rp.UserId = $user
        WHERE rm.ItemId IS NULL
        ORDER BY
            CASE WHEN rp.State = 1 THEN 0 ELSE 1 END,
            CASE WHEN rp.State = 1 THEN rp.UpdatedAt END DESC,
            cn.SortKey,
            cn.Id
        LIMIT 1;
        """;

    private const string RollupReference = """
        WITH RECURSIVE subtree(RootId, NodeId) AS (
            SELECT r.Id, r.Id FROM catalog_nodes r WHERE r.Id IN ({ids})
            UNION ALL
            SELECT s.RootId, cn.Id FROM subtree s
            JOIN catalog_nodes cn ON cn.ParentId = s.NodeId
        )
        SELECT s.RootId,
               COUNT(*) AS Total,
               SUM(CASE WHEN rm.ItemId IS NOT NULL THEN 1 ELSE 0 END) AS ReadCount,
               SUM(CASE WHEN rm.ItemId IS NULL AND rp.State = 1 THEN 1 ELSE 0 END) AS InProgressCount
        FROM subtree s
        JOIN catalog_nodes a ON a.Id = s.NodeId AND a.Kind = 1 AND a.Availability != 5
        LEFT JOIN read_marks rm ON rm.ItemId = a.Id AND rm.UserId = $user
        LEFT JOIN reading_progress rp ON rp.ItemId = a.Id AND rp.UserId = $user
        GROUP BY s.RootId;
        """;

    private static async Task<List<object[]>> RowsAsync(MangaPixerDbContext db, string sql, params (string Name, object? Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            command.Parameters.Add(p);
        }
        var rows = new List<object[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task FolderCovers_GivesTheReferenceRows_ForEveryFolder_AloneAndTogether(int seed)
    {
        var s = await SeedAsync(seed);
        await using var _ = s.Db;
        Assert.True(s.Folders.Count > 10);

        async Task CompareAsync(IReadOnlyCollection<long> ids)
        {
            var reference = (await RowsAsync(s.Db, FolderCoversReference.Replace("{ids}", string.Join(",", ids), StringComparison.Ordinal)))
                .ToDictionary(r => (long)r[0], r => ((long)r[1], (string)r[2]));
            var rewritten = await FolderCovers.ResolveArchivesAsync(s.Db, ids, CancellationToken.None);
            Assert.Equal(reference.OrderBy(kv => kv.Key), rewritten.OrderBy(kv => kv.Key));
        }

        foreach (var (id, _) in s.Folders)
            await CompareAsync([id]);
        await CompareAsync(s.Folders.Select(f => f.Id).ToList());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(42)]
    public async Task NextUnreadWalk_GivesTheReferenceArchive_InEveryFolderAndAtTheRoot_ForEachViewer(int seed)
    {
        var s = await SeedAsync(seed);
        await using var _ = s.Db;
        var service = new CatalogBrowseService(s.Db, new LibraryAuthorizationService(s.Db));
        var asked = 0;
        foreach (var user in s.Users)
        {
            var places = s.Folders.Select(f => (f.LibraryId, Parent: (long?)f.Id)).Concat(s.Libraries.Select(l => (LibraryId: l, Parent: (long?)null)));
            foreach (var (library, parent) in places)
            {
                var reference = await RowsAsync(s.Db, NextUnreadReference, ("$lib", library), ("$parent", parent), ("$user", user));
                var rewritten = await service.ResolveNextUnreadByWalkAsync(library, parent, user, CancellationToken.None);
                Assert.Equal(reference.Count == 0 ? null : (string)reference[0][0], rewritten?.Id);
                asked++;
            }
        }
        Assert.True(asked > 20);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(19)]
    [InlineData(42)]
    public async Task ReadRollup_GivesTheReferenceCounts_ForEveryFolder_AloneAndTogether_ForEachViewer(int seed)
    {
        var s = await SeedAsync(seed);
        await using var _ = s.Db;
        var service = new CatalogBrowseService(s.Db, new LibraryAuthorizationService(s.Db));
        var compared = 0;

        async Task CompareAsync(List<long> ids, long user)
        {
            var reference = new Dictionary<long, FolderReadRollup>();
            foreach (var r in await RowsAsync(s.Db, RollupReference.Replace("{ids}", string.Join(",", ids), StringComparison.Ordinal), ("$user", user)))
            {
                if (FolderReadRollupRules.Classify((int)(long)r[1], (int)(long)r[2], (int)(long)r[3]) is { } rollup)
                    reference[(long)r[0]] = rollup;
            }
            var rewritten = await service.ResolveFolderReadRollupsAsync(ids, user, CancellationToken.None);
            Assert.Equal(reference.OrderBy(kv => kv.Key), rewritten.OrderBy(kv => kv.Key));
            compared += reference.Count;
        }

        foreach (var user in s.Users)
        {
            foreach (var (id, _) in s.Folders)
                await CompareAsync([id], user);
            await CompareAsync(s.Folders.Select(f => f.Id).ToList(), user);
        }
        Assert.True(compared > 10);
    }
}
