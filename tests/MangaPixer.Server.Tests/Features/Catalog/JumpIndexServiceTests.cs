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
        string sortKey,
        long? parentId = null)
    {
        var node = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
            LibraryId = libraryId,
            ParentId = parentId,
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
    public async Task GetJumpIndex_MixedScripts_BucketsByFirstCharacterInListingOrder()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            // One folder and eight archives: the folder is one leading "Folders" chip (1.41.0), the archives carry the letters.
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Folder, "Apple", SortKey.ForNode(CatalogNodeKind.Folder, "Apple"));
            foreach (var name in new[] { "Avocado", "Banana", "Cherry", "12 Monkeys", "あいうえお", "漫画", "Книга", "!!!" })
                await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, name, SortKey.ForNode(CatalogNodeKind.Archive, name));

            var service = new JumpIndexService(db);
            var result = await service.GetJumpIndexAsync(userId, libraryId);

            // Listing order (1.41.0: chips follow the listing, each one contiguous run): the folder, then "!!!" and "12 Monkeys"
            // (punctuation and digits sort before letters: "#"), the letters, then the scripts by code point.
            Assert.Equal(new[] { "Folders", "#", "A", "B", "C", "Cyrillic", "Kana", "CJK" }, result.Buckets.Select(b => b.Label).ToArray());
            Assert.Equal(2, result.Buckets.Single(b => b.Label == "#").Count);
            Assert.Equal("letters", result.Mode);
            Assert.Equal("asc", result.Direction);
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
    public async Task GetJumpIndex_HashBucketLeadsRail_AndTheMinorityFilesChipLandsOnTheFirstArchive()
    {
        // "#" leads the rail because punctuation- and digit-leading names sort first (1.41.0: a leading "-" is no longer skipped,
        // so "-Bonus" is in "#" where the listing shows it, not in a "B" bucket at the top of the list). Three folders and two
        // archives: the folders carry the letters, the archives are one trailing "Files" chip.
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Folder, "-Bonus", SortKey.ForNode(CatalogNodeKind.Folder, "-Bonus"));
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Folder, "Apple", SortKey.ForNode(CatalogNodeKind.Folder, "Apple"));
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Folder, "10-Title", SortKey.ForNode(CatalogNodeKind.Folder, "10-Title"));
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Banana", SortKey.ForNode(CatalogNodeKind.Archive, "Banana"));
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "7-Title", SortKey.ForNode(CatalogNodeKind.Archive, "7-Title"));

            var jumpService = new JumpIndexService(db);
            var browseService = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

            var index = await jumpService.GetJumpIndexAsync(userId, libraryId);

            Assert.Equal(new[] { "#", "A", "Files" }, index.Buckets.Select(b => b.Label).ToArray());
            Assert.Equal(new[] { 2, 1, 2 }, index.Buckets.Select(b => b.Count).ToArray());
            Assert.Null(index.Buckets[0].FirstCursor);
            Assert.Equal(SortKey.ForNode(CatalogNodeKind.Folder, "10-Title"), index.Buckets[1].FirstCursor);

            string[] expected = ["-Bonus", "Apple", "7-Title"];
            for (var i = 0; i < expected.Length; i++)
            {
                var page = await browseService.BrowseAsync(
                    userId, libraryId, parentId: null, cursor: index.Buckets[i].FirstCursor, pageSize: 50, sort: "name");
                Assert.Equal(expected[i], page.Items[0].DisplayName);
                Assert.Equal(page.Items[0].Id, index.Buckets[i].FirstNodeId);
            }
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
    public async Task GetJumpIndex_FoldersAndArchives_TheSmallerKindIsOneChipAtItsPosition()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            // Folder "Apple" sorts before the archives by the kind prefix. Before 1.41.0 one "A" bucket spanned both kinds
            // (non-contiguous when other folders sat between them); now the folder is its own leading chip.
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Folder, "Apple", SortKey.ForNode(CatalogNodeKind.Folder, "Apple"));
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Apple Pie", SortKey.ForNode(CatalogNodeKind.Archive, "Apple Pie"));
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, "Apricot", SortKey.ForNode(CatalogNodeKind.Archive, "Apricot"));

            var service = new JumpIndexService(db);
            var result = await service.GetJumpIndexAsync(userId, libraryId);

            Assert.Equal(new[] { "Folders", "A" }, result.Buckets.Select(b => b.Label).ToArray());
            Assert.Equal(new[] { 1, 2 }, result.Buckets.Select(b => b.Count).ToArray());
            Assert.Null(result.Buckets[0].FirstCursor);
            Assert.Equal(SortKey.ForNode(CatalogNodeKind.Folder, "Apple"), result.Buckets[1].FirstCursor);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetJumpIndex_NumberedFolder_EveryBucketLandsOnItsFirstRow_BothDirections()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var folder = await AddNodeAsync(db, libraryId, CatalogNodeKind.Folder, "Numbered Saga", SortKey.ForNode(CatalogNodeKind.Folder, "Numbered Saga"));
            await AddNodeAsync(db, libraryId, CatalogNodeKind.Folder, "Other Saga", SortKey.ForNode(CatalogNodeKind.Folder, "Other Saga"));
            var names = Enumerable.Range(1, 103).Select(n => $"Numbered Saga - Ch. {n:0000}.cbz").Append("Numbered Saga - Ch. 0050.5.cbz").ToList();
            foreach (var name in names)
                await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, name, SortKey.ForNode(CatalogNodeKind.Archive, name), folder.Id);

            var jumpService = new JumpIndexService(db);
            var browseService = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

            foreach (var descending in new[] { false, true })
            {
                var index = await jumpService.GetJumpIndexAsync(userId, libraryId, folder.Id, descending, group: null);
                Assert.Equal("numbers", index.Mode);
                Assert.Equal(descending ? "desc" : "asc", index.Direction);
                Assert.Equal(folder.PublicId, index.ParentId);
                var labels = new[] { "0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "100" };
                Assert.Equal(descending ? labels.Reverse().ToArray() : labels, index.Buckets.Select(b => b.Label).ToArray());
                Assert.Equal(104, index.Buckets.Sum(b => b.Count));

                // Walk the whole listing page by page from each bucket's cursor: the first Count rows are the bucket's rows.
                var seen = 0;
                foreach (var bucket in index.Buckets)
                {
                    var page = await browseService.BrowseAsync(userId, libraryId, folder.Id, bucket.FirstCursor, pageSize: bucket.Count,
                        direction: descending ? SortDirection.Descending : SortDirection.Ascending, sort: "name");
                    Assert.Equal(bucket.FirstNodeId, page.Items[0].Id);
                    Assert.Equal(bucket.Count, page.Items.Count);
                    var low = int.Parse(bucket.Label, System.Globalization.CultureInfo.InvariantCulture);
                    Assert.Equal(low == 100 ? "100-109" : $"{low}-{low + 9}", bucket.Range);
                    Assert.All(page.Items, item =>
                    {
                        var number = decimal.Parse(item.DisplayName["Numbered Saga - Ch. ".Length..].Replace(".cbz", ""), System.Globalization.CultureInfo.InvariantCulture);
                        Assert.InRange(number, low, low + 9.99m);
                    });
                    seen += page.Items.Count;
                }
                Assert.Equal(104, seen);
            }
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetJumpIndex_VolumesView_HidesTheRail_UnlessTheViewerAsksForTheFlatList()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            // Chapters that state their volume: the folder has a Volumes view (on by default), which pages by volume.
            var folder = await AddNodeAsync(db, libraryId, CatalogNodeKind.Folder, "Stacked Saga", SortKey.ForNode(CatalogNodeKind.Folder, "Stacked Saga"));
            for (var c = 1; c <= 12; c++)
            {
                var name = $"Stacked Saga v{(c - 1) / 4 + 1:00} c{c:000}.cbz";
                await AddNodeAsync(db, libraryId, CatalogNodeKind.Archive, name, SortKey.ForNode(CatalogNodeKind.Archive, name), folder.Id);
            }

            var service = new JumpIndexService(db);

            var shown = await service.GetJumpIndexAsync(userId, libraryId, folder.Id, descending: false, group: null);
            Assert.Empty(shown.Buckets);
            var asked = await service.GetJumpIndexAsync(userId, libraryId, folder.Id, descending: false, group: "volumes");
            Assert.Empty(asked.Buckets);

            var flat = await service.GetJumpIndexAsync(userId, libraryId, folder.Id, descending: false, group: "flat");
            Assert.Equal("numbers", flat.Mode);
            Assert.Equal(new[] { "0", "5", "10" }, flat.Buckets.Select(b => b.Label).ToArray());
        }
        finally
        {
            await db.DisposeAsync();
        }
    }
}
