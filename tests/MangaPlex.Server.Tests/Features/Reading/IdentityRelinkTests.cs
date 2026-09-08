namespace com.lifepixer.mangaplex.Tests.Server.Features.Reading;

using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Features.Reading;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
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
    private readonly DbContextOptions<MangaPlexDbContext> _options;

    public IdentityRelinkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-relink-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        var cs = DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "relink.db"));
        _options = new DbContextOptionsBuilder<MangaPlexDbContext>().UseSqlite(cs).Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPlexDbContext db, long userId, long libraryId)> BaseAsync()
    {
        var db = new MangaPlexDbContext(_options);
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
        MangaPlexDbContext db, long libraryId, long publicId, string name,
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

    private static async Task AddProgressAsync(MangaPlexDbContext db, long userId, long itemId, int ordinal)
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

    private static IdentityRelinkService Service(MangaPlexDbContext db) =>
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
}
