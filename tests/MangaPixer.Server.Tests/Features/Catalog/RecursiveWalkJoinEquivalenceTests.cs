namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using System.Data.Common;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Home;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Features.Metadata.Review;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.47.0 lane S2: the recursive walks whose join-back now drives from the walk (<c>CROSS JOIN</c>) give the SAME rows as their earlier
/// text, kept verbatim here as the reference - the home stacks' readable archives, the info panel's entries below a folder, the review's
/// duplicate names, a folder's archives for Mark read (also in the same ascending-id order the earlier full-scan plan gave), the
/// picker's linked series below a folder, and the latest-added maintenance. A deterministic pseudo-random tree in two libraries:
/// nested folders, tombstoned folders and archives, SortKey ties, series links of every state (with and without a record), facet
/// entries on folders and archives, varied archive dates. Every folder is asked alone and all of them at once.
/// Real file-backed SQLite with the production connection settings; synthetic names.
/// </summary>
public sealed class RecursiveWalkJoinEquivalenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public RecursiveWalkJoinEquivalenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-walks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "walks.db")))
            .Options;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private sealed record Seeded(MangaPixerDbContext Db, List<long> Folders);

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
        var records = new List<long>();
        for (var r = 0; r < 4; r++)
        {
            var record = new MetadataRecordEntity
            {
                PublicId = "rec" + r,
                Provider = "mangaupdates",
                ExternalId = "ext" + r,
                Title = "Synthetic record " + r,
                FetchedAt = DateTimeOffset.UtcNow,
            };
            db.MetadataRecords.Add(record);
            await db.SaveChangesAsync();
            records.Add(record.Id);
        }

        var serial = 0;
        var folders = new List<long>();
        var nodes = new List<(long Id, long LibraryId)>();
        string[] names = ["Alpha 001", "Alpha 002", "Beta v01", "Beta v02", "Gamma c010", "gamma c010", "Delta", "Alpha 001"];
        var start = DateTimeOffset.UtcNow.AddDays(-100);
        async Task FillAsync(long lib, long? parent, int depth)
        {
            var children = depth == 0 ? 10 : random.Next(0, 5);
            for (var c = 0; c < children; c++)
            {
                var folder = depth < 5 && random.NextDouble() < (depth < 2 ? 0.6 : 0.35);
                var kind = folder ? CatalogNodeKind.Folder : CatalogNodeKind.Archive;
                var name = names[random.Next(names.Length)];
                var node = new CatalogNodeEntity
                {
                    PublicId = OpaqueId.Encode(1000 + ++serial),
                    LibraryId = lib,
                    ParentId = parent,
                    Kind = (int)kind,
                    DisplayName = name,
                    RelativePath = $"/synthetic/n{serial}",
                    PathKey = $"/synthetic/n{serial}",
                    SortKey = com.lifepixer.mangapixer.Core.Ordering.SortKey.ForNode(kind, name),
                    Availability = random.NextDouble() < 0.15 ? (int)CatalogNodeAvailability.Tombstoned : (int)CatalogNodeAvailability.Available,
                    CreatedAt = start.AddHours(random.Next(0, 2000)),
                };
                db.CatalogNodes.Add(node);
                await db.SaveChangesAsync();
                nodes.Add((node.Id, lib));
                if (random.NextDouble() < 0.3)
                {
                    var state = (SeriesLinkState)random.Next(0, 6);
                    db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
                    {
                        NodeId = node.Id,
                        LibraryId = lib,
                        State = (int)state,
                        RecordId = random.NextDouble() < 0.8 ? records[random.Next(records.Count)] : null,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
                }
                if (folder)
                {
                    folders.Add(node.Id);
                    await FillAsync(lib, node.Id, depth + 1);
                }
                else
                {
                    db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = node.Id, ContentVersion = 1, PageCount = 5, ByteLength = 1 });
                }
            }
        }
        foreach (var lib in libraries)
            await FillAsync(lib, null, 0);
        await db.SaveChangesAsync();

        // Facet works, values and entries (each value at most once per location: the table's unique key).
        await ExecAsync(db, "INSERT INTO facet_works (WorkKey, Kind, Title, SortKey, UpdatedAt) VALUES " +
            string.Join(",", Enumerable.Range(0, 8).Select(w => $"('w{w}', 0, 'Work {w}', 'work {w}', 0)")));
        await ExecAsync(db, "INSERT INTO facet_values (PublicId, Bucket, ValueKey, Display) VALUES " +
            string.Join(",", Enumerable.Range(0, 6).Select(v => $"('v{v}', {v % 3}, 'k{v}', 'Value {v}')")));
        var works = await IdsAsync(db, "SELECT Id FROM facet_works");
        var values = await IdsAsync(db, "SELECT Id FROM facet_values");
        var entries = new List<string>();
        foreach (var (id, lib) in nodes)
        {
            if (random.NextDouble() >= 0.4)
                continue;
            foreach (var value in values.Where(_ => random.NextDouble() < 0.4))
                entries.Add($"({works[random.Next(works.Count)]}, {id}, {lib}, {value}, 1, {random.Next(0, 4)}, {random.Next(0, 2)})");
        }
        if (entries.Count > 0)
            await ExecAsync(db, "INSERT INTO facet_entries (WorkId, LocationNodeId, LibraryId, ValueId, Sources, Roles, Conflict) VALUES " + string.Join(",", entries));
        return new Seeded(db, folders);
    }

    private static async Task ExecAsync(MangaPixerDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<long>> IdsAsync(MangaPixerDbContext db, string sql) =>
        (await RowsAsync(db, sql)).Select(r => (long)r[0]).ToList();

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

    /// <summary>The rows as a sorted multiset of text lines (the order is compared separately where the caller depends on it).</summary>
    private static List<string> Bag(IEnumerable<object[]> rows) =>
        rows.Select(r => string.Join("|", r.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))).Order(StringComparer.Ordinal).ToList();

    private static string Ids(IEnumerable<long> ids) => string.Join(",", ids);

    // ---- The statements before lane S2, verbatim (the reference).

    private const string ReadableArchivesReference = """
        WITH RECURSIVE subtree(RootId, NodeId) AS (
            SELECT r.Id, r.Id FROM catalog_nodes r WHERE r.Id IN ({ids})
            UNION ALL
            SELECT s.RootId, cn.Id FROM subtree s
            JOIN catalog_nodes cn ON cn.ParentId = s.NodeId
        )
        SELECT s.RootId, a.Id
        FROM subtree s
        JOIN catalog_nodes a ON a.Id = s.NodeId AND a.Kind = 1 AND a.Availability != 5;
        """;

    private const string EntriesBelowReference = """
        WITH RECURSIVE below(Id) AS (
            SELECT Id FROM catalog_nodes WHERE ParentId = @node
            UNION ALL
            SELECT c.Id FROM catalog_nodes c JOIN below ON c.ParentId = below.Id
        )
        SELECT e.WorkId, e.ValueId, e.LocationNodeId, e.Roles, e.Conflict
        FROM below
        JOIN facet_entries e ON e.LocationNodeId = below.Id
        JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != 5
        """;

    private const string DuplicatesReference = """
        WITH RECURSIVE sub(root, id) AS (
            SELECT n."Id", n."Id" FROM catalog_nodes n WHERE n."Id" IN ({ids})
            UNION ALL
            SELECT sub.root, c."Id" FROM catalog_nodes c JOIN sub ON c."ParentId" = sub.id WHERE c."Availability" != 5
        )
        SELECT sub.root AS "Root", c."ParentId" AS "ParentId", p."DisplayName" AS "ParentName", c."DisplayName" AS "Name"
        FROM sub JOIN catalog_nodes c ON c."Id" = sub.id JOIN catalog_nodes p ON p."Id" = c."ParentId"
        WHERE c."Kind" = 1
        """;

    private const string DescendantArchivesReference = """
        WITH RECURSIVE descendants(Id) AS (
            SELECT Id FROM catalog_nodes WHERE ParentId = $root
            UNION ALL
            SELECT cn.Id FROM catalog_nodes cn
            JOIN descendants d ON cn.ParentId = d.Id
        )
        SELECT cn.Id
        FROM catalog_nodes cn
        JOIN descendants d ON cn.Id = d.Id
        WHERE cn.Kind = 1 AND cn.Availability != 5;
        """;

    private const string LinkedSeriesBelowReference = """
        WITH RECURSIVE d(NodeId, Path, Depth) AS (
            SELECT cn.Id, cn.SortKey, 1 FROM catalog_nodes cn WHERE cn.ParentId = {folder}
            UNION ALL
            SELECT cn.Id, d.Path || char(1) || cn.SortKey, d.Depth + 1 FROM d JOIN catalog_nodes cn ON cn.ParentId = d.NodeId
            WHERE d.Depth < 64
        )
        SELECT cn.Id, cn.PublicId, cn.DisplayName, l.RecordId, d.Path
        FROM d
        JOIN catalog_nodes cn ON cn.Id = d.NodeId
        JOIN node_series_links l ON l.NodeId = d.NodeId
        WHERE cn.Availability != 5 AND l.RecordId IS NOT NULL
          AND l.State IN ({states});
        """;

    private const string LatestDescendantReference = """
        WITH RECURSIVE descendants(RootId, NodeId) AS (
            SELECT Id, Id FROM catalog_nodes WHERE Kind = 0
            UNION ALL
            SELECT d.RootId, cn.Id FROM descendants d
            JOIN catalog_nodes cn ON cn.ParentId = d.NodeId
        ),
        maxes AS (
            SELECT d.RootId AS RootId, MAX(a.CreatedAt) AS MaxCreated
            FROM descendants d
            JOIN catalog_nodes a ON a.Id = d.NodeId AND a.Kind = 1 AND a.Availability != 5
            GROUP BY d.RootId
        )
        UPDATE catalog_nodes
        SET LatestDescendantAddedAt = (SELECT MaxCreated FROM maxes WHERE maxes.RootId = catalog_nodes.Id)
        WHERE catalog_nodes.Kind = 0;
        """;

    private static async Task CompareEachAndAllAsync(Seeded s, Func<string, string> reference, Func<string, string> current)
    {
        foreach (var folder in s.Folders)
        {
            var id = folder.ToString(CultureInfo.InvariantCulture);
            Assert.Equal(Bag(await RowsAsync(s.Db, reference(id))), Bag(await RowsAsync(s.Db, current(id))));
        }
        var all = Ids(s.Folders);
        var expected = Bag(await RowsAsync(s.Db, reference(all)));
        Assert.NotEmpty(expected);
        Assert.Equal(expected, Bag(await RowsAsync(s.Db, current(all))));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task HomeReadableArchives_GiveTheReferenceRows(int seed)
    {
        var s = await SeedAsync(seed);
        await using var _ = s.Db;
        Assert.True(s.Folders.Count > 10);
        await CompareEachAndAllAsync(s, ids => ReadableArchivesReference.Replace("{ids}", ids, StringComparison.Ordinal),
            RecentChaptersService.ReadableArchivesSql);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task ReviewDuplicateNames_GiveTheReferenceRows(int seed)
    {
        var s = await SeedAsync(seed);
        await using var _ = s.Db;
        await CompareEachAndAllAsync(s, ids => DuplicatesReference.Replace("{ids}", ids, StringComparison.Ordinal), MetadataReviewService.DuplicatesSql);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task InfoPanelEntriesBelow_GiveTheReferenceRows(int seed)
    {
        var s = await SeedAsync(seed);
        await using var _ = s.Db;
        var compared = 0;
        foreach (var folder in s.Folders)
        {
            var expected = Bag(await RowsAsync(s.Db, EntriesBelowReference, ("@node", folder)));
            Assert.Equal(expected, Bag(await RowsAsync(s.Db, InfoPanelService.EntriesBelowSql, ("@node", folder))));
            compared += expected.Count;
        }
        Assert.True(compared > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task MarkReadArchives_GiveTheReferenceRows_InAscendingIdOrder(int seed)
    {
        var s = await SeedAsync(seed);
        await using var _ = s.Db;
        var compared = 0;
        foreach (var folder in s.Folders)
        {
            var expected = (await RowsAsync(s.Db, DescendantArchivesReference, ("$root", folder))).Select(r => (long)r[0]).Order().ToList();
            var current = (await RowsAsync(s.Db, ReadingStateService.DescendantArchivesSql, ("$root", folder))).Select(r => (long)r[0]).ToList();
            Assert.Equal(expected, current); // the same ids, ascending - the order the earlier full-scan plan wrote the read marks in
            compared += expected.Count;
        }
        Assert.True(compared > 10);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task PickerLinkedSeriesBelow_GiveTheReferenceRows(int seed)
    {
        var s = await SeedAsync(seed);
        await using var _ = s.Db;
        var states = string.Join(",", Enum.GetValues<SeriesLinkState>().Where(SeriesLinkStates.IsSeries)
            .Select(st => ((int)st).ToString(CultureInfo.InvariantCulture)));
        var compared = 0;
        foreach (var folder in s.Folders)
        {
            var reference = LinkedSeriesBelowReference.Replace("{folder}", folder.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{states}", states, StringComparison.Ordinal);
            var expected = Bag(await RowsAsync(s.Db, reference));
            Assert.Equal(expected, Bag(await RowsAsync(s.Db, CoverPickerService.LinkedSeriesBelowSql(folder))));
            compared += expected.Count;
        }
        Assert.True(compared > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task LatestDescendantAddedAt_GivesTheReferenceValues_ForAllFoldersAndForSome(int seed)
    {
        var s = await SeedAsync(seed);
        await using var _ = s.Db;
        const string Snapshot = "SELECT Id, LatestDescendantAddedAt FROM catalog_nodes WHERE Kind = 0 ORDER BY Id";
        const string Clear = "UPDATE catalog_nodes SET LatestDescendantAddedAt = NULL";

        await ExecAsync(s.Db, Clear);
        await ExecAsync(s.Db, LatestDescendantReference);
        var expected = Bag(await RowsAsync(s.Db, Snapshot));
        Assert.Contains(expected, line => !line.EndsWith('|'));

        await ExecAsync(s.Db, Clear);
        await LatestDescendantAddedAtMaintenance.RecomputeAllFoldersAsync(s.Db);
        Assert.Equal(expected, Bag(await RowsAsync(s.Db, Snapshot)));

        // The affected-path form over every other folder: those get the reference values, the rest stay cleared.
        await ExecAsync(s.Db, Clear);
        var some = s.Folders.Where((_, i) => i % 2 == 0).ToHashSet();
        await LatestDescendantAddedAtMaintenance.RecomputeFoldersAsync(s.Db, some);
        var partial = await RowsAsync(s.Db, Snapshot);
        var reference = (await RowsAsync(s.Db, Snapshot)).Count;
        Assert.Equal(reference, partial.Count);
        var expectedById = expected.ToDictionary(l => l[..l.IndexOf('|', StringComparison.Ordinal)], l => l);
        foreach (var row in partial)
        {
            var id = (long)row[0];
            var line = string.Join("|", row.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
            if (some.Contains(id))
                Assert.Equal(expectedById[id.ToString(CultureInfo.InvariantCulture)], line);
            else
                Assert.True(row[1] is DBNull, "a folder outside the affected set keeps its value");
        }
    }
}
