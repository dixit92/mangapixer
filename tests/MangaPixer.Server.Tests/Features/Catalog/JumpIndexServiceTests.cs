using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

/// <summary>
/// Service-with-DB tests for JumpIndexService.
/// Uses real file-backed SQLite. Verifies bucketing across mixed-script
/// fixtures and cursor correctness against the browse endpoint's name sort.
/// </summary>
public sealed class JumpIndexServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public JumpIndexServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-jump-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "jump.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext db, long userId, long libraryId)> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var library = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Test Library",
            RootPath = "/private/test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

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

        return (db, user.Id, library.Id);
    }

    private static async Task<CatalogNodeEntity> AddNodeAsync(
        MangaPixerDbContext db,
        long libraryId,
        CatalogNodeKind kind,
        string displayName,
        string sortKey)
    {
        var node = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
            LibraryId = libraryId,
            ParentId = null,
            Kind = (int)kind,
            DisplayName = displayName,
            RelativePath = "/private/" + displayName,
            PathKey = "/private/" + displayName.ToLowerInvariant(),
            SortKey = sortKey,
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node;
    }

    [Fact]
    public async Task GetJumpIndex_MixedScripts_BucketsByFirstCollationElement()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            // Latin letters (folders sort before archives by SortKey prefix).
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Folder, "Apple", "0\u001f0Apple");
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Avocado", "0\u001f1Avocado");
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Banana", "0\u001f1Banana");
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Cherry", "0\u001f1Cherry");
            // Digits
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "12 Monkeys", "0\u001f1D12Z099Monkeys");
            // Kana
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "あいうえお", "0\u001f1あいうえお");
            // CJK
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "漫画", "0\u001f1漫画");
            // Cyrillic
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Книга", "0\u001f1Книга");
            // Symbols (Other)
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "!!!", "0\u001f1!!!");

            var service = new JumpIndexService(db);
            var result = await service.GetJumpIndexAsync(userId, libraryId);

            var labels = result.Buckets.Select(b => b.Label).ToList();
            // Rail order: #, A, B, C, Kana, CJK, Cyrillic, Other. "#" leads the
            // rail as the numeric/symbol bucket (by convention, as in a typical
            // A–Z index); its cursor still lands on the first numeric node in
            // persisted SortKey order.
            Assert.Equal(new[] { "#", "A", "B", "C", "Kana", "CJK", "Cyrillic", "Other" }, labels);

            var a = result.Buckets.First(b => b.Label == "A");
            Assert.Equal(2, a.Count); // Apple + Avocado

            var hash = result.Buckets.First(b => b.Label == "#");
            Assert.Equal(1, hash.Count);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetJumpIndex_FirstBucketCursorIsNull()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Alpha", "0\u001f1Alpha");
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Beta", "0\u001f1Beta");
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Gamma", "0\u001f1Gamma");

            var service = new JumpIndexService(db);
            var result = await service.GetJumpIndexAsync(userId, libraryId);

            var first = result.Buckets[0];
            Assert.Null(first.FirstCursor);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetJumpIndex_CursorLandsOnBucketFirstNode()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            // Three distinct letters, each with one archive.
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Alpha", "0\u001f1Alpha");
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Beta", "0\u001f1Beta");
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Gamma", "0\u001f1Gamma");

            var service = new JumpIndexService(db);
            var result = await service.GetJumpIndexAsync(userId, libraryId);

            // The "B" bucket's cursor must be the SortKey of "Alpha" (the node
            // immediately before the bucket's first node), so the browse
            // endpoint's exclusive `SortKey > cursor` filter lands on "Beta".
            var bBucket = result.Buckets.First(b => b.Label == "B");
            Assert.NotNull(bBucket.FirstCursor);
            Assert.Equal("0\u001f1Alpha", bBucket.FirstCursor);

            // The "C"/"Gamma" bucket's cursor must be the SortKey of "Beta".
            var gBucket = result.Buckets.First(b => b.Label == "G");
            Assert.NotNull(gBucket.FirstCursor);
            Assert.Equal("0\u001f1Beta", gBucket.FirstCursor);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetJumpIndex_CursorWorksAgainstBrowseNameSort()
    {
        // End-to-end: the bucket cursor must produce a browse page whose first
        // item is the bucket's first node. This is the real contract.
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Alpha", "0\u001f1Alpha");
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Beta", "0\u001f1Beta");
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Gamma", "0\u001f1Gamma");
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Delta", "0\u001f1Delta");

            var jumpService = new JumpIndexService(db);
            var browseService = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

            var index = await jumpService.GetJumpIndexAsync(userId, libraryId);
            var deltaBucket = index.Buckets.First(b => b.Label == "D");
            Assert.NotNull(deltaBucket.FirstCursor);

            var page = await browseService.BrowseAsync(
                userId, libraryId, parentId: null, cursor: deltaBucket.FirstCursor,
                pageSize: 50, sort: "name");

            // The first item must be "Delta" — the bucket's first node.
            Assert.NotEmpty(page.Items);
            Assert.Equal("Delta", page.Items[0].DisplayName);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetJumpIndex_HashBucketLeadsRailAndCursorLandsOnFirstNumericNode()
    {
        // "#" must lead the rail, and its cursor must land on the first numeric
        // node in browse name-sort order — even when a letter folder sorts ahead
        // of the numeric nodes (within folders, SortKey is ordinal, so 'A' < 'D'
        // and a letter folder precedes a numeric folder). Uses real SortKeys via
        // SortKey.ForNode so the cursor contract is exercised against the actual
        // browse ordering, not hand-crafted keys.
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var root = SortKey.ForLibraryRoot();
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Folder, "Apple", SortKey.ForNode(CatalogNodeKind.Folder, "Apple", root));
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Folder, "10-Title", SortKey.ForNode(CatalogNodeKind.Folder, "10-Title", root));
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Banana", SortKey.ForNode(CatalogNodeKind.Archive, "Banana", root));
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "7-Title", SortKey.ForNode(CatalogNodeKind.Archive, "7-Title", root));

            var jumpService = new JumpIndexService(db);
            var browseService = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

            var index = await jumpService.GetJumpIndexAsync(userId, libraryId);

            // "#" leads the rail.
            Assert.Equal("#", index.Buckets[0].Label);

            var hash = index.Buckets.First(b => b.Label == "#");
            // The first numeric node is the "10-Title" folder (it follows the
            // "Apple" letter folder in SortKey order), so the "#" cursor is the
            // Apple SortKey — non-null, because "#" is NOT the global first node.
            Assert.NotNull(hash.FirstCursor);
            Assert.Equal(SortKey.ForNode(CatalogNodeKind.Folder, "Apple", root), hash.FirstCursor);

            // The "A" bucket's first node ("Apple") IS the global first node, so
            // its cursor is null regardless of rail position.
            var a = index.Buckets.First(b => b.Label == "A");
            Assert.Null(a.FirstCursor);

            // End-to-end: passing the "#" cursor to browse lands on "10-Title",
            // the first numeric node — not the end of the listing.
            var page = await browseService.BrowseAsync(
                userId, libraryId, parentId: null, cursor: hash.FirstCursor,
                pageSize: 50, sort: "name");
            Assert.NotEmpty(page.Items);
            Assert.Equal("10-Title", page.Items[0].DisplayName);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetJumpIndex_ExcludesTombstoned()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Active", "0\u001f1Active");
            var tomb = await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Gone", "0\u001f1Gone");
            tomb.Availability = (int)CatalogNodeAvailability.Tombstoned;
            await db.SaveChangesAsync();

            var service = new JumpIndexService(db);
            var result = await service.GetJumpIndexAsync(userId, libraryId);

            Assert.Single(result.Buckets);
            Assert.Equal("A", result.Buckets[0].Label);
            Assert.Equal(1, result.Buckets[0].Count);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetJumpIndex_UnauthorizedUser_ReturnsEmpty()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Test", "0\u001f1Test");

            var reader = new UserEntity
            {
                PublicId = OpaqueId.Encode(Random.Shared.NextInt64(10, long.MaxValue)),
                UserName = "reader",
                NormalizedUserName = "READER",
                IsActive = true,
                IsAdmin = false,
                PasswordHash = "hash",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(reader);
            await db.SaveChangesAsync();

            var service = new JumpIndexService(db);
            var result = await service.GetJumpIndexAsync(reader.Id, libraryId);

            Assert.Empty(result.Buckets);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetJumpIndex_EmptyLibrary_ReturnsNoBuckets()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var service = new JumpIndexService(db);
            var result = await service.GetJumpIndexAsync(userId, libraryId);

            Assert.Empty(result.Buckets);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetJumpIndex_FoldersAndArchivesSameLetterMerged()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            // Folder "Apple" sorts before archive "Apple Pie" by the prefix.
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Folder, "Apple", "0\u001f0Apple");
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Apple Pie", "0\u001f1Apple Pie");
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Apricot", "0\u001f1Apricot");

            var service = new JumpIndexService(db);
            var result = await service.GetJumpIndexAsync(userId, libraryId);

            var a = result.Buckets.First(b => b.Label == "A");
            Assert.Equal(3, a.Count);
            // The "A" bucket is the first bucket → null cursor (start of listing).
            Assert.Null(a.FirstCursor);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public void BucketLabel_LatinLetters_MapToSingleLetter()
    {
        Assert.Equal("A", JumpIndexService.BucketLabelFor("Apple"));
        Assert.Equal("A", JumpIndexService.BucketLabelFor("avocado"));
        Assert.Equal("Z", JumpIndexService.BucketLabelFor("Zebra"));
    }

    [Fact]
    public void BucketLabel_Digits_MapToHash()
    {
        Assert.Equal("#", JumpIndexService.BucketLabelFor("12 Monkeys"));
        Assert.Equal("#", JumpIndexService.BucketLabelFor("007"));
    }

    [Fact]
    public void BucketLabel_ScriptGroups_MapToScriptName()
    {
        Assert.Equal("Kana", JumpIndexService.BucketLabelFor("あいうえお"));
        Assert.Equal("Kana", JumpIndexService.BucketLabelFor("カタカナ"));
        Assert.Equal("Hangul", JumpIndexService.BucketLabelFor("한국어"));
        Assert.Equal("CJK", JumpIndexService.BucketLabelFor("漫画"));
        Assert.Equal("Cyrillic", JumpIndexService.BucketLabelFor("Книга"));
        Assert.Equal("Greek", JumpIndexService.BucketLabelFor("Βιβλίο"));
        Assert.Equal("Arabic", JumpIndexService.BucketLabelFor("كتاب"));
        Assert.Equal("Hebrew", JumpIndexService.BucketLabelFor("ספר"));
        Assert.Equal("Thai", JumpIndexService.BucketLabelFor("หนังสือ"));
    }

    [Fact]
    public void BucketLabel_SymbolsAndEmpty_MapToOther()
    {
        Assert.Equal("Other", JumpIndexService.BucketLabelFor("!!!"));
        Assert.Equal("Other", JumpIndexService.BucketLabelFor(""));
        Assert.Equal("Other", JumpIndexService.BucketLabelFor("   "));
    }

    [Fact]
    public void BucketLabel_SkipsLeadingPunctuation()
    {
        Assert.Equal("A", JumpIndexService.BucketLabelFor("[Archive]"));
        Assert.Equal("Q", JumpIndexService.BucketLabelFor("\"Quoted\""));
        Assert.Equal("T", JumpIndexService.BucketLabelFor("The Apple"));
    }

    [Fact]
    public void RailRank_HashThenLatinAZThenScriptsThenOther()
    {
        Assert.Equal(0, JumpIndexService.RailRank("#"));
        Assert.Equal(1, JumpIndexService.RailRank("A"));
        Assert.Equal(26, JumpIndexService.RailRank("Z"));
        Assert.Equal(27, JumpIndexService.RailRank("Kana"));
        Assert.Equal(28, JumpIndexService.RailRank("Hangul"));
        Assert.Equal(29, JumpIndexService.RailRank("CJK"));
        Assert.Equal(30, JumpIndexService.RailRank("Cyrillic"));
        Assert.Equal(99, JumpIndexService.RailRank("Other"));
        // # sorts before A, which sorts before scripts, which sort before Other.
        Assert.True(JumpIndexService.RailRank("#") < JumpIndexService.RailRank("A"));
        Assert.True(JumpIndexService.RailRank("A") < JumpIndexService.RailRank("Kana"));
        Assert.True(JumpIndexService.RailRank("Thai") < JumpIndexService.RailRank("Other"));
    }
}
