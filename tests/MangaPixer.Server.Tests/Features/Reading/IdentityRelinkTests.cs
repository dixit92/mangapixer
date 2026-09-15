namespace com.lifepixer.mangapixer.Tests.Server.Features.Reading;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Integration tests for identity relink on move/duplicate/replace
/// (§2.2 criterion 7: "moves preserve identity only with sufficiently strong
/// evidence; ambiguity is exposed rather than guessed").
///
/// Model of a move: the old archive node is tombstoned and a new node appears in
/// the same library carrying the same strong (SHA-256) hash. Reading progress is
/// tied to the old node id; the relink service moves it to the new node when — and
/// only when — the evidence is unambiguous.
/// </summary>
public sealed class IdentityRelinkTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public IdentityRelinkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-relink-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        var cs = DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "relink.db"));
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>().UseSqlite(cs).Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext db, long userId, long libraryId)> BaseAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var library = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Test",
            RootPath = "/private/test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        var user = new UserEntity
        {
            PublicId = OpaqueId.Encode(2),
            UserName = "reader",
            NormalizedUserName = "READER",
            IsActive = true,
            IsAdmin = true, // admin ⇒ CanAccessLibrary true for all libraries
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (db, user.Id, library.Id);
    }

    private static async Task<long> AddArchiveAsync(
        MangaPixerDbContext db, long libraryId, long publicId, string name,
        string? strongHash, int availability, int contentVersion = 1)
    {
        var node = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(publicId),
            LibraryId = libraryId,
            ParentId = null,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = name,
            RelativePath = "/private/" + name,
            PathKey = "/private/" + name,
            SortKey = "1" + name,
            Availability = availability,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();

        db.ArchiveItems.Add(new ArchiveItemEntity
        {
            NodeId = node.Id,
            ArchiveFormat = 0,
            ByteLength = 1024,
            ModificationTicks = 0,
            ContentVersion = contentVersion,
            AnalysisState = 0,
            PageCount = 10,
            StrongHash = strongHash,
            StrongHashSourceVersion = strongHash is null ? 0 : 1,
        });
        await db.SaveChangesAsync();
        return node.Id;
    }

    private static async Task AddProgressAsync(MangaPixerDbContext db, long userId, long itemId, int ordinal)
    {
        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = userId,
            ItemId = itemId,
            ContentVersion = 1,
            Ordinal = ordinal,
            EntryKey = "e" + ordinal,
            State = 1, // in progress
            Revision = 1,
            LastMutationId = Guid.NewGuid().ToString("N"),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static IdentityRelinkService Service(MangaPixerDbContext db) =>
        new(db, new LibraryAuthorizationService(db));

    [Fact]
    public async Task Move_UniqueHash_AutoRelinksProgressToNewItem()
    {
        var (db, userId, libraryId) = await BaseAsync();
        // Old item (moved away → tombstoned) and the same content reappearing under
        // a new node with the same strong hash.
        var oldId = await AddArchiveAsync(db, libraryId, 100, "old.cbz", "HASH-A", availability: 5 /*tombstoned*/);
        var newId = await AddArchiveAsync(db, libraryId, 101, "moved.cbz", "HASH-A", availability: 0 /*available*/);
        await AddProgressAsync(db, userId, oldId, ordinal: 7);

        var result = await Service(db).AutoRelinkByHashAsync(userId, oldId);

        Assert.Equal(RelinkStatus.Success, result.Status);
        Assert.Null(await db.ReadingProgress.FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == oldId));
        var moved = await db.ReadingProgress.FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == newId);
        Assert.NotNull(moved);
        Assert.Equal(7, moved!.Ordinal); // position preserved
    }

    [Fact]
    public async Task Move_AmbiguousHash_IsExposedNotGuessed_ProgressUntouched()
    {
        var (db, userId, libraryId) = await BaseAsync();
        var oldId = await AddArchiveAsync(db, libraryId, 100, "old.cbz", "HASH-DUP", availability: 5);
        // Two available candidates share the hash → ambiguous.
        await AddArchiveAsync(db, libraryId, 101, "dup1.cbz", "HASH-DUP", availability: 0);
        await AddArchiveAsync(db, libraryId, 102, "dup2.cbz", "HASH-DUP", availability: 0);
        await AddProgressAsync(db, userId, oldId, ordinal: 3);

        var result = await Service(db).AutoRelinkByHashAsync(userId, oldId);

        Assert.Equal(RelinkStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.CandidateCount);
        // Progress is NOT guessed onto either candidate — it stays on the old item.
        Assert.NotNull(await db.ReadingProgress.FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == oldId));
    }

    [Fact]
    public async Task Move_NoMatchingHash_ReturnsNoMatch()
    {
        var (db, userId, libraryId) = await BaseAsync();
        var oldId = await AddArchiveAsync(db, libraryId, 100, "old.cbz", "HASH-A", availability: 5);
        await AddArchiveAsync(db, libraryId, 101, "other.cbz", "HASH-B", availability: 0);
        await AddProgressAsync(db, userId, oldId, ordinal: 2);

        var result = await Service(db).AutoRelinkByHashAsync(userId, oldId);

        Assert.Equal(RelinkStatus.NoMatch, result.Status);
    }

    [Fact]
    public async Task ManualRelink_IsConflictSafe_UnlessOverwriteConfirmed()
    {
        var (db, userId, libraryId) = await BaseAsync();
        var oldId = await AddArchiveAsync(db, libraryId, 100, "old.cbz", "HASH-A", availability: 5);
        var targetId = await AddArchiveAsync(db, libraryId, 101, "target.cbz", "HASH-A", availability: 0);
        await AddProgressAsync(db, userId, oldId, ordinal: 9);
        await AddProgressAsync(db, userId, targetId, ordinal: 1); // target already has progress

        // Without overwrite: refuses to clobber existing target progress.
        var conflict = await Service(db).ManualRelinkAsync(userId, oldId, targetId, overwrite: false);
        Assert.Equal(RelinkStatus.Conflict, conflict.Status);
        var target = await db.ReadingProgress.FirstAsync(p => p.UserId == userId && p.ItemId == targetId);
        Assert.Equal(1, target.Ordinal); // unchanged

        // With overwrite: the move is applied.
        var ok = await Service(db).ManualRelinkAsync(userId, oldId, targetId, overwrite: true);
        Assert.Equal(RelinkStatus.Success, ok.Status);
        var overwritten = await db.ReadingProgress.FirstAsync(p => p.UserId == userId && p.ItemId == targetId);
        Assert.Equal(9, overwritten.Ordinal);
        Assert.Null(await db.ReadingProgress.FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == oldId));
    }

    [Fact]
    public async Task AutoRelink_OntoItemWithExistingProgress_DoesNotThrowAndDropsOrphanedRow()
    {
        // Regression for the unguarded relink insert path. Auto-relink (overwrite=false)
        // onto a target that already has progress would previously INSERT a second
        // (UserId, ItemId) row and hit the unique index uncaught — a 500. The hardened
        // path keeps the target's own progress, drops the orphaned old row, and relinks
        // bookmarks without throwing.
        var (db, userId, libraryId) = await BaseAsync();
        var oldId = await AddArchiveAsync(db, libraryId, 100, "old.cbz", "HASH-A", availability: 5);
        var targetId = await AddArchiveAsync(db, libraryId, 101, "target.cbz", "HASH-A", availability: 0);
        await AddProgressAsync(db, userId, oldId, ordinal: 9);
        await AddProgressAsync(db, userId, targetId, ordinal: 1); // target already has progress

        var result = await Service(db).AutoRelinkByHashAsync(userId, oldId);

        Assert.Equal(RelinkStatus.Success, result.Status);

        // The orphaned old row is gone.
        Assert.Null(await db.ReadingProgress.FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == oldId));

        // The target keeps its own progress (not overwritten by the auto-relink).
        var target = await db.ReadingProgress.FirstAsync(p => p.UserId == userId && p.ItemId == targetId);
        Assert.Equal(1, target.Ordinal);

        // Exactly one row for the target — no duplicate slipped through the race.
        var count = await db.ReadingProgress.CountAsync(p => p.UserId == userId && p.ItemId == targetId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ManualRelink_OverwriteOntoConcurrentTarget_Insert_RaceRecovered()
    {
        // Regression for the relink insert race under overwrite=true. A concurrent
        // request creates the target row between the relink's load and its insert,
        // so the relink's INSERT hits the unique index. The hardened path recovers
        // by reloading the concurrent row and re-applying the old item's state as an
        // update (overwrite semantics preserved), instead of throwing a 500.
        var (seedDb, userId, libraryId) = await BaseAsync();
        var oldId = await AddArchiveAsync(seedDb, libraryId, 100, "old.cbz", "HASH-A", availability: 5);
        var targetId = await AddArchiveAsync(seedDb, libraryId, 101, "target.cbz", "HASH-A", availability: 0);
        await AddProgressAsync(seedDb, userId, oldId, ordinal: 9);
        await seedDb.DisposeAsync();

        // Concurrent writer creates the target row first (simulating the race).
        await using (var racer = new MangaPixerDbContext(_options))
        {
            await AddProgressAsync(racer, userId, targetId, ordinal: 1);
        }

        // The relink now sees no target row at load time, inserts, and collides.
        // Recovery must reload the concurrent row and overwrite it with old's state.
        await using var db = new MangaPixerDbContext(_options);
        var result = await Service(db).ManualRelinkAsync(userId, oldId, targetId, overwrite: true);

        Assert.Equal(RelinkStatus.Success, result.Status);
        var target = await db.ReadingProgress.FirstAsync(p => p.UserId == userId && p.ItemId == targetId);
        Assert.Equal(9, target.Ordinal); // overwritten with old's state
        Assert.Null(await db.ReadingProgress.FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == oldId));
        var count = await db.ReadingProgress.CountAsync(p => p.UserId == userId && p.ItemId == targetId);
        Assert.Equal(1, count);
    }
}
