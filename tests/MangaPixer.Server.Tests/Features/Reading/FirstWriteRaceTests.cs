namespace com.lifepixer.mangapixer.Tests.Server.Features.Reading;

using System.Data.Common;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

/// <summary>
/// The first-write race on the per-user read state, FORCED deterministically: an EF interceptor lets a "racer"
/// (a second context on the same SQLite file) insert its row right after the service has read that the row does
/// not exist and before the service inserts its own. Every path that inserts a <c>reading_progress</c> /
/// <c>read_marks</c> row must recover (no exception, the right final state, exactly one row), including the ones
/// that used to be an unhandled 500 (manual mark-read, bulk folder mark-read, a completion that races only on the
/// sticky read mark).
/// </summary>
public sealed class FirstWriteRaceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-race-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RaceInjector _injector = new();
    private readonly DbContextOptions<MangaPixerDbContext> _options;
    private readonly DbContextOptions<MangaPixerDbContext> _plainOptions;

    public FirstWriteRaceTests()
    {
        Directory.CreateDirectory(_tempDir);
        var connectionString = DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "race.db"));
        _plainOptions = new DbContextOptionsBuilder<MangaPixerDbContext>().UseSqlite(connectionString).Options;
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>().UseSqlite(connectionString).AddInterceptors(_injector).Options;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private sealed record Seed(long UserId, long FolderId, long ArchiveA, long ArchiveB);

    private async Task<Seed> SeedAsync()
    {
        await using var db = new MangaPixerDbContext(_plainOptions);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var library = new LibraryEntity { PublicId = OpaqueId.Encode(1), DisplayName = "Race", RootPath = "/private/race", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        db.Users.Add(new UserEntity
        {
            PublicId = OpaqueId.Encode(2),
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var userId = db.Users.Single().Id;

        CatalogNodeEntity Node(long publicId, CatalogNodeKind kind, string name, long? parent) => new()
        {
            PublicId = OpaqueId.Encode(publicId),
            LibraryId = library.Id,
            ParentId = parent,
            Kind = (int)kind,
            DisplayName = name,
            RelativePath = "/private/" + name,
            PathKey = "/private/" + name,
            SortKey = "1" + name,
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var folder = Node(100, CatalogNodeKind.Folder, "F", null);
        db.CatalogNodes.Add(folder);
        await db.SaveChangesAsync();
        var a = Node(101, CatalogNodeKind.Archive, "a.cbz", folder.Id);
        var b = Node(102, CatalogNodeKind.Archive, "b.cbz", folder.Id);
        db.CatalogNodes.AddRange(a, b);
        await db.SaveChangesAsync();
        foreach (var node in new[] { a, b })
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = node.Id, ArchiveFormat = 0, ByteLength = 1024, ModificationTicks = 0, ContentVersion = 1, AnalysisState = 0, PageCount = 10 });
        await db.SaveChangesAsync();
        return new Seed(userId, folder.Id, a.Id, b.Id);
    }

    private ReadingStateService CreateService(MangaPixerDbContext db) => new(db, new LibraryAuthorizationService(db));

    private Func<Task> InsertMark(long userId, long itemId) => async () =>
    {
        await using var racer = new MangaPixerDbContext(_plainOptions);
        racer.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = itemId, MarkedAt = DateTimeOffset.UtcNow, Source = "racer" });
        await racer.SaveChangesAsync();
    };

    private Func<Task> InsertProgress(long userId, long itemId, int page) => async () =>
    {
        await using var racer = new MangaPixerDbContext(_plainOptions);
        racer.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = userId,
            ItemId = itemId,
            ContentVersion = 1,
            EntryKey = OpaqueId.Encode(page),
            Ordinal = page,
            State = (int)ReadingState.InProgress,
            Revision = 1,
            LastMutationId = "racer",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await racer.SaveChangesAsync();
    };

    private async Task<(int Progress, int Marks)> CountsAsync(long userId, long itemId)
    {
        await using var db = new MangaPixerDbContext(_plainOptions);
        return (await db.ReadingProgress.CountAsync(p => p.UserId == userId && p.ItemId == itemId),
            await db.ReadMarks.CountAsync(m => m.UserId == userId && m.ItemId == itemId));
    }

    [Fact]
    public async Task UpdateProgress_ProgressRowCreatedByARacer_IsReappliedAsAnUpdate()
    {
        var seed = await SeedAsync();
        _injector.Arm("reading_progress", InsertProgress(seed.UserId, seed.ArchiveA, page: 1));
        await using var db = new MangaPixerDbContext(_options);

        var result = await CreateService(db).UpdateProgressAsync(seed.UserId, seed.ArchiveA, pageIndex: 4, expectedContentVersion: 1, mutationId: "mine");

        Assert.True(_injector.Fired, "The racer never ran; the test proves nothing");
        Assert.Equal(UpdateStatus.Success, result.Status);
        Assert.Equal((1, 0), await CountsAsync(seed.UserId, seed.ArchiveA));
        await using var verify = new MangaPixerDbContext(_plainOptions);
        var row = await verify.ReadingProgress.SingleAsync(p => p.ItemId == seed.ArchiveA);
        Assert.Equal(4, row.Ordinal);
        Assert.Equal("mine", row.LastMutationId);
        Assert.Equal(2, row.Revision);
    }

    [Fact]
    public async Task UpdateProgress_CompletionRacingOnlyOnTheReadMark_Recovers()
    {
        var seed = await SeedAsync();
        // The progress row exists (so the save is an UPDATE plus a mark INSERT); a racer creates the mark.
        await InsertProgress(seed.UserId, seed.ArchiveA, page: 1)();
        _injector.Arm("read_marks", InsertMark(seed.UserId, seed.ArchiveA));
        await using var db = new MangaPixerDbContext(_options);

        var result = await CreateService(db).UpdateProgressAsync(seed.UserId, seed.ArchiveA, pageIndex: 9, expectedContentVersion: 1, mutationId: "mine");

        Assert.True(_injector.Fired, "The racer never ran; the test proves nothing");
        Assert.Equal(UpdateStatus.Success, result.Status);
        Assert.Equal((1, 1), await CountsAsync(seed.UserId, seed.ArchiveA));
        await using var verify = new MangaPixerDbContext(_plainOptions);
        var row = await verify.ReadingProgress.SingleAsync(p => p.ItemId == seed.ArchiveA);
        Assert.Equal((int)ReadingState.Completed, row.State);
        Assert.Equal(9, row.Ordinal);
    }

    [Fact]
    public async Task UpdateProgress_FirstCompletionLosingBothRows_Recovers()
    {
        var seed = await SeedAsync();
        // A racer finishes the same item first: progress AND mark. The loser inserts both and loses both.
        _injector.Arm("read_marks", async () =>
        {
            await InsertProgress(seed.UserId, seed.ArchiveA, page: 9)();
            await InsertMark(seed.UserId, seed.ArchiveA)();
        });
        await using var db = new MangaPixerDbContext(_options);

        var result = await CreateService(db).UpdateProgressAsync(seed.UserId, seed.ArchiveA, pageIndex: 9, expectedContentVersion: 1, mutationId: "mine");

        Assert.True(_injector.Fired, "The racer never ran; the test proves nothing");
        Assert.Equal(UpdateStatus.Success, result.Status);
        Assert.Equal((1, 1), await CountsAsync(seed.UserId, seed.ArchiveA));
    }

    [Fact]
    public async Task SetItemRead_MarkCreatedByARacer_IsIdempotent()
    {
        var seed = await SeedAsync();
        _injector.Arm("read_marks", InsertMark(seed.UserId, seed.ArchiveA));
        await using var db = new MangaPixerDbContext(_options);

        var ok = await CreateService(db).SetItemReadAsync(seed.UserId, seed.ArchiveA, read: true);

        Assert.True(_injector.Fired, "The racer never ran; the test proves nothing");
        Assert.True(ok);
        Assert.Equal((1, 1), await CountsAsync(seed.UserId, seed.ArchiveA));
        await using var verify = new MangaPixerDbContext(_plainOptions);
        var row = await verify.ReadingProgress.SingleAsync(p => p.ItemId == seed.ArchiveA);
        Assert.Equal((int)ReadingState.Completed, row.State);
        Assert.Equal(9, row.Ordinal);
    }

    [Fact]
    public async Task SetItemRead_ProgressCreatedByARacer_IsCompletedNotDuplicated()
    {
        var seed = await SeedAsync();
        _injector.Arm("reading_progress", InsertProgress(seed.UserId, seed.ArchiveA, page: 2));
        await using var db = new MangaPixerDbContext(_options);

        var ok = await CreateService(db).SetItemReadAsync(seed.UserId, seed.ArchiveA, read: true);

        Assert.True(_injector.Fired, "The racer never ran; the test proves nothing");
        Assert.True(ok);
        Assert.Equal((1, 1), await CountsAsync(seed.UserId, seed.ArchiveA));
        await using var verify = new MangaPixerDbContext(_plainOptions);
        var row = await verify.ReadingProgress.SingleAsync(p => p.ItemId == seed.ArchiveA);
        Assert.Equal((int)ReadingState.Completed, row.State);
    }

    [Fact]
    public async Task SetFolderRead_MarkCreatedByARacer_CountsOnlyTheMarksItAdded()
    {
        var seed = await SeedAsync();
        _injector.Arm("read_marks", InsertMark(seed.UserId, seed.ArchiveA));
        await using var db = new MangaPixerDbContext(_options);

        var result = await CreateService(db).SetFolderReadAsync(seed.UserId, seed.FolderId, read: true);

        Assert.True(_injector.Fired, "The racer never ran; the test proves nothing");
        Assert.NotNull(result);
        Assert.Equal(2, result!.Total);
        // The racer marked A; the recovered save added only B.
        Assert.Equal(1, result.Affected);
        Assert.Equal((0, 1), await CountsAsync(seed.UserId, seed.ArchiveA));
        Assert.Equal((0, 1), await CountsAsync(seed.UserId, seed.ArchiveB));
    }

    /// <summary>
    /// After the next READ of <c>table</c> by the service under test, runs the racer once. A reader command is
    /// recognised by its table name in the SQL; the racer uses its own context, so its commands are not injected
    /// into again (it is built from the interceptor-free options).
    /// </summary>
    private sealed class RaceInjector : DbCommandInterceptor
    {
        private string? _table;
        private Func<Task>? _racer;

        public bool Fired { get; private set; }

        public void Arm(string table, Func<Task> racer)
        {
            _table = table;
            _racer = racer;
            Fired = false;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (_racer is { } racer && _table is { } table && command.CommandText.Contains($"FROM \"{table}\"", StringComparison.Ordinal))
            {
                _racer = null;
                Fired = true;
                await racer();
            }

            return result;
        }
    }
}
