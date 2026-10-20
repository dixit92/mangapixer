namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.46.0 (lane B, measured cost #5): the "Continue" archive. (1) At the library root it comes from a direct query instead of the
/// whole-library walk - the SAME answer, checked against the walk (<see cref="CatalogBrowseService.ResolveNextUnreadByWalkAsync"/>) through a
/// sequence of reading states on a tree with a tombstoned folder holding live archives that sort first (more than one candidate round), a
/// tombstoned archive, a live folder below a tombstoned one, another library and a second viewer. (2) It is answered on the first page only.
/// Real file-backed SQLite; synthetic names.
/// </summary>
public sealed class NextUnreadRootTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public NextUnreadRootTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-nextunread-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "nextunread.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private sealed class Tree
    {
        public required MangaPixerDbContext Db { get; init; }
        public required long LibraryId { get; init; }
        public required long OtherLibraryId { get; init; }
        public required long UserId { get; init; }
        public required long OtherUserId { get; init; }
        public required List<CatalogNodeEntity> Reachable { get; init; }
        public required List<CatalogNodeEntity> Unreachable { get; init; }
    }

    private async Task<Tree> SeedAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        var library = new LibraryEntity { PublicId = OpaqueId.Encode(11), DisplayName = "Lib", RootPath = "/private/a", CreatedAt = DateTimeOffset.UtcNow };
        var other = new LibraryEntity { PublicId = OpaqueId.Encode(12), DisplayName = "Other", RootPath = "/private/b", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.AddRange(library, other);
        UserEntity User(int n) => new()
        {
            PublicId = OpaqueId.Encode(100 + n),
            UserName = $"viewer{n}",
            NormalizedUserName = $"VIEWER{n}",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var user = User(1);
        var second = User(2);
        db.Users.AddRange(user, second);
        await db.SaveChangesAsync();

        var serial = 0;
        async Task<CatalogNodeEntity> NodeAsync(long lib, long? parent, CatalogNodeKind kind, string name, bool tombstoned = false)
        {
            var node = new CatalogNodeEntity
            {
                PublicId = OpaqueId.Encode(1000 + ++serial),
                LibraryId = lib,
                ParentId = parent,
                Kind = (int)kind,
                DisplayName = name,
                RelativePath = $"/private/n{serial}",
                PathKey = $"/private/n{serial}",
                SortKey = SortKey.ForNode(kind, name),
                Availability = tombstoned ? (int)CatalogNodeAvailability.Tombstoned : (int)CatalogNodeAvailability.Available,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();
            if (kind == CatalogNodeKind.Archive)
            {
                db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = node.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 7, ByteLength = 1 });
                await db.SaveChangesAsync();
            }
            return node;
        }

        var reachable = new List<CatalogNodeEntity>();
        var unreachable = new List<CatalogNodeEntity>();
        // A tombstoned folder whose 20 LIVE archives sort before everything else (more than one candidate round of 16).
        var gone = await NodeAsync(library.Id, null, CatalogNodeKind.Folder, "Gone Folder", tombstoned: true);
        for (var i = 0; i < 20; i++)
            unreachable.Add(await NodeAsync(library.Id, gone.Id, CatalogNodeKind.Archive, $"Aaa {i:00}"));
        // A live folder below the tombstoned one, with a live archive.
        var below = await NodeAsync(library.Id, gone.Id, CatalogNodeKind.Folder, "Below");
        unreachable.Add(await NodeAsync(library.Id, below.Id, CatalogNodeKind.Archive, "Aab 00"));
        // Live series with nested folders and a tombstoned archive.
        var alpha = await NodeAsync(library.Id, null, CatalogNodeKind.Folder, "Alpha");
        reachable.Add(await NodeAsync(library.Id, alpha.Id, CatalogNodeKind.Archive, "Alpha 001"));
        reachable.Add(await NodeAsync(library.Id, alpha.Id, CatalogNodeKind.Archive, "Alpha 002"));
        await NodeAsync(library.Id, alpha.Id, CatalogNodeKind.Archive, "Alpha 000", tombstoned: true);
        var sub = await NodeAsync(library.Id, alpha.Id, CatalogNodeKind.Folder, "Extras");
        reachable.Add(await NodeAsync(library.Id, sub.Id, CatalogNodeKind.Archive, "Alpha 003"));
        var bravo = await NodeAsync(library.Id, null, CatalogNodeKind.Folder, "Bravo");
        reachable.Add(await NodeAsync(library.Id, bravo.Id, CatalogNodeKind.Archive, "Bravo 001"));
        reachable.Add(await NodeAsync(library.Id, null, CatalogNodeKind.Archive, "Zulu Loose"));
        // Another library sorting first: never a candidate here.
        var foreign = await NodeAsync(other.Id, null, CatalogNodeKind.Folder, "Aaaa Other");
        await NodeAsync(other.Id, foreign.Id, CatalogNodeKind.Archive, "Aaaa 001");

        return new Tree
        {
            Db = db,
            LibraryId = library.Id,
            OtherLibraryId = other.Id,
            UserId = user.Id,
            OtherUserId = second.Id,
            Reachable = reachable,
            Unreachable = unreachable,
        };
    }

    private static async Task<(string? Root, string? Walk)> BothAsync(Tree t, long userId)
    {
        var service = new CatalogBrowseService(t.Db, new LibraryAuthorizationService(t.Db));
        var root = await service.ResolveNextUnreadAtRootAsync(t.LibraryId, userId, CancellationToken.None);
        var walk = await service.ResolveNextUnreadByWalkAsync(t.LibraryId, null, userId, CancellationToken.None);
        return (root is null ? null : JsonSerializer.Serialize(root), walk is null ? null : JsonSerializer.Serialize(walk));
    }

    private static async Task MarkReadAsync(Tree t, CatalogNodeEntity node)
    {
        t.Db.ReadMarks.Add(new ReadMarkEntity { UserId = t.UserId, ItemId = node.Id, MarkedAt = DateTimeOffset.UtcNow });
        await t.Db.SaveChangesAsync();
    }

    private static async Task ProgressAsync(Tree t, CatalogNodeEntity node, DateTimeOffset at, int state = 1)
    {
        t.Db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = t.UserId,
            ItemId = node.Id,
            ContentVersion = 1,
            EntryKey = "entry1",
            Ordinal = 3,
            State = state,
            Revision = 1,
            LastMutationId = Guid.NewGuid().ToString("N"),
            UpdatedAt = at,
        });
        await t.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task RootQuery_GivesTheWalksAnswer_ThroughEveryReadingState()
    {
        var t = await SeedAsync();
        await using var _ = t.Db;
        var expectedIds = new List<string?>();

        async Task CheckAsync(long userId, string? expectedName)
        {
            var (root, walk) = await BothAsync(t, userId);
            Assert.Equal(walk, root);
            var name = root is null ? null : JsonDocument.Parse(root).RootElement.GetProperty("DisplayName").GetString();
            Assert.Equal(expectedName, name);
            expectedIds.Add(name);
        }

        // Nothing read: the first archive by SortKey the walk can reach (the 21 below the tombstoned folder are skipped).
        await CheckAsync(t.UserId, "Alpha 001");
        await CheckAsync(t.OtherUserId, "Alpha 001");
        // Read ones are skipped.
        await MarkReadAsync(t, t.Reachable[0]);
        await MarkReadAsync(t, t.Reachable[1]);
        await CheckAsync(t.UserId, "Alpha 003");
        // An in-progress archive wins; one below the tombstoned folder never does, even when it is the most recent.
        var now = DateTimeOffset.UtcNow;
        await ProgressAsync(t, t.Reachable[3], now.AddMinutes(-10));
        await ProgressAsync(t, t.Unreachable[0], now);
        await CheckAsync(t.UserId, "Bravo 001");
        // The most recently updated in-progress archive wins; a finished progress row (state 2) is not in progress.
        await ProgressAsync(t, t.Reachable[4], now.AddMinutes(-5));
        await ProgressAsync(t, t.Reachable[2], now.AddMinutes(-1), state: 2);
        await CheckAsync(t.UserId, "Zulu Loose");
        // An in-progress archive that is also marked read is not a candidate.
        await MarkReadAsync(t, t.Reachable[4]);
        await CheckAsync(t.UserId, "Bravo 001");
        // Every reachable archive read: null, although unreachable ones are unread.
        await MarkReadAsync(t, t.Reachable[2]);
        await MarkReadAsync(t, t.Reachable[3]);
        await CheckAsync(t.UserId, null);
        await CheckAsync(t.OtherUserId, "Alpha 001");
        Assert.Equal(8, expectedIds.Count);
    }

    [Fact]
    public async Task Continue_IsAnsweredOnTheFirstPageOnly()
    {
        var t = await SeedAsync();
        await using var _ = t.Db;
        var service = new CatalogBrowseService(t.Db, new LibraryAuthorizationService(t.Db));

        var first = await service.BrowseAsync(t.UserId, t.LibraryId, parentId: null, cursor: null, pageSize: 2);
        Assert.Equal("Alpha 001", first.NextUnread?.DisplayName);
        Assert.True(first.HasMore);

        var later = await service.BrowseAsync(t.UserId, t.LibraryId, parentId: null, cursor: first.NextCursor, pageSize: 2);
        Assert.NotEmpty(later.Items);
        Assert.Null(later.NextUnread);

        var earlier = await service.BrowseAsync(t.UserId, t.LibraryId, parentId: null, cursor: null, pageSize: 2, before: later.Items[0].SortKeyOrName());
        Assert.Null(earlier.NextUnread);

        // A folder's first page still answers it (the walk), a later page of it does not.
        var alpha = t.Reachable[0].ParentId!.Value;
        var folderFirst = await service.BrowseAsync(t.UserId, t.LibraryId, parentId: alpha, cursor: null, pageSize: 1);
        Assert.Equal("Alpha 001", folderFirst.NextUnread?.DisplayName);
        var folderLater = await service.BrowseAsync(t.UserId, t.LibraryId, parentId: alpha, cursor: folderFirst.NextCursor, pageSize: 1);
        Assert.Null(folderLater.NextUnread);
    }
}

internal static class NextUnreadTestExtensions
{
    /// <summary>A name-sort backward position for a card: its stored sort key (the browse cursor of name sort).</summary>
    public static string SortKeyOrName(this CatalogNodeDto node) => SortKey.ForNode(node.Kind, node.DisplayName);
}
