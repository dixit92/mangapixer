namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Collections.Concurrent;
using System.Net;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The 1.43.0 tracker seam: every COMMITTED read-mark insert / delete reaches <see cref="IReadMarkObserver"/> once, whichever
/// service made it; a mark that already existed and a save that failed signal nothing. Service-with-DB tests drive
/// <see cref="ReadingStateService"/> on a context carrying the interceptor; the HTTP test proves the app's own DbContext is wired.
/// </summary>
public sealed class ReadMarkObserverTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-rmobs-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RecordingObserver _observer = new();
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public ReadMarkObserverTests()
    {
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "marks.db")))
            .AddInterceptors(new ReadMarkChangeInterceptor(_observer, NullLogger<ReadMarkChangeInterceptor>.Instance))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ManualMarkRead_SignalsOnce_AndNeverForAMarkThatAlreadyExists()
    {
        await using var db = await CreateAsync();
        var (userId, items) = await SeedAsync(db, archives: 1);
        var service = new ReadingStateService(db, new LibraryAuthorizationService(db));

        Assert.True(await service.SetItemReadAsync(userId, items[0], read: true));
        Assert.True(await service.SetItemReadAsync(userId, items[0], read: true));

        var change = Assert.Single(_observer.Changes);
        Assert.Equal((ReadMarkChangeKind.Added, userId, items[0], "manual"), (change.Kind, change.UserId, change.ItemId, change.Source));
    }

    [Fact]
    public async Task FinishingInTheReader_SignalsACompletionMark()
    {
        await using var db = await CreateAsync();
        var (userId, items) = await SeedAsync(db, archives: 1);
        var service = new ReadingStateService(db, new LibraryAuthorizationService(db));

        await service.UpdateProgressAsync(userId, items[0], pageIndex: 3, expectedContentVersion: 1, mutationId: "m-1");
        Assert.Empty(_observer.Changes);
        await service.UpdateProgressAsync(userId, items[0], pageIndex: 9, expectedContentVersion: 1, mutationId: "m-2");

        var change = Assert.Single(_observer.Changes);
        Assert.Equal((ReadMarkChangeKind.Added, items[0], "completion"), (change.Kind, change.ItemId, change.Source));
    }

    [Fact]
    public async Task FolderMarkRead_SignalsOnlyTheNewMarks_AndUnreadSignalsTheRemoval()
    {
        await using var db = await CreateAsync();
        var (userId, items) = await SeedAsync(db, archives: 3);
        var service = new ReadingStateService(db, new LibraryAuthorizationService(db));
        Assert.True(await service.SetItemReadAsync(userId, items[1], read: true));
        _observer.Clear();

        var folderId = (await db.CatalogNodes.SingleAsync(n => n.Kind == (int)CatalogNodeKind.Folder)).Id;
        Assert.NotNull(await service.SetFolderReadAsync(userId, folderId, read: true));

        Assert.Equal(new[] { items[0], items[2] }, _observer.Changes.Select(c => c.ItemId).Order().ToArray());
        Assert.All(_observer.Changes, c => Assert.Equal((ReadMarkChangeKind.Added, "bulk"), (c.Kind, c.Source)));

        _observer.Clear();
        Assert.True(await service.SetItemReadAsync(userId, items[1], read: false));
        var removed = Assert.Single(_observer.Changes);
        Assert.Equal((ReadMarkChangeKind.Removed, items[1], "manual"), (removed.Kind, removed.ItemId, removed.Source));
    }

    [Fact]
    public async Task AFailedSave_SignalsNothing()
    {
        await using var db = await CreateAsync();
        var (userId, items) = await SeedAsync(db, archives: 1);
        db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = items[0], MarkedAt = DateTimeOffset.UtcNow, Source = "manual" });
        await db.SaveChangesAsync();
        _observer.Clear();

        // A second mark for the same user + item breaks the unique index: the save fails and nothing is signalled.
        await using var other = new MangaPixerDbContext(_options);
        other.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = items[0], MarkedAt = DateTimeOffset.UtcNow, Source = "manual" });
        await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());

        Assert.Empty(_observer.Changes);
    }

    [Fact]
    public async Task AThrowingObserver_DoesNotFailTheSave()
    {
        var options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "throwing.db")))
            .AddInterceptors(new ReadMarkChangeInterceptor(new ThrowingObserver(), NullLogger<ReadMarkChangeInterceptor>.Instance))
            .Options;
        await using var db = new MangaPixerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        var (userId, items) = await SeedAsync(db, archives: 1);
        var service = new ReadingStateService(db, new LibraryAuthorizationService(db));

        Assert.True(await service.SetItemReadAsync(userId, items[0], read: true));
        Assert.True(await db.ReadMarks.AnyAsync(m => m.UserId == userId && m.ItemId == items[0]));
    }

    [Fact]
    public async Task TheAppsDbContext_IsWired_AMarkReadThroughTheApiReachesTheObserver()
    {
        var recorder = new RecordingObserver();
        using var factory = new ApiTokenTestFactory(configureServices: services =>
        {
            services.RemoveAll<IReadMarkObserver>();
            services.AddSingleton<IReadMarkObserver>(recorder);
        });
        var admin = await factory.AdminAsync();
        string itemPublicId;
        long itemId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var library = new LibraryEntity { PublicId = "rmo-lib", DisplayName = "RMO", RootPath = "/tmp/rmo", CreatedAt = DateTimeOffset.UtcNow };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            var node = Archive(library.Id, null, "rmo-1");
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();
            db.ArchiveItems.Add(Item(node.Id));
            await db.SaveChangesAsync();
            (itemPublicId, itemId) = (node.PublicId, node.Id);
        }
        recorder.Clear();

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/v1/reading/{itemPublicId}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/v1/reading/{itemPublicId}/read", null)).StatusCode);
        var added = Assert.Single(recorder.Changes);
        Assert.Equal((ReadMarkChangeKind.Added, itemId, "manual"), (added.Kind, added.ItemId, added.Source));

        recorder.Clear();
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/v1/reading/{itemPublicId}/read")).StatusCode);
        Assert.Equal(ReadMarkChangeKind.Removed, Assert.Single(recorder.Changes).Kind);
    }

    private async Task<MangaPixerDbContext> CreateAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        return db;
    }

    /// <summary>A user (admin) and a folder holding <paramref name="archives"/> archives of 10 pages; returns the archive ids in order.</summary>
    private static async Task<(long UserId, List<long> Items)> SeedAsync(MangaPixerDbContext db, int archives)
    {
        var library = new LibraryEntity { PublicId = OpaqueId.Encode(1), DisplayName = "L", RootPath = "/private/l", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        var user = new UserEntity
        {
            PublicId = OpaqueId.Encode(2),
            UserName = "reader",
            NormalizedUserName = "READER",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var folder = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(10),
            LibraryId = library.Id,
            Kind = (int)CatalogNodeKind.Folder,
            DisplayName = "Folder",
            RelativePath = "Folder",
            PathKey = "folder",
            SortKey = "1Folder",
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(folder);
        await db.SaveChangesAsync();

        var items = new List<long>();
        for (var i = 0; i < archives; i++)
        {
            var node = Archive(library.Id, folder.Id, OpaqueId.Encode(100 + i));
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();
            db.ArchiveItems.Add(Item(node.Id));
            await db.SaveChangesAsync();
            items.Add(node.Id);
        }
        db.ChangeTracker.Clear();
        return (user.Id, items);
    }

    private static CatalogNodeEntity Archive(long libraryId, long? parentId, string publicId) => new()
    {
        PublicId = publicId,
        LibraryId = libraryId,
        ParentId = parentId,
        Kind = (int)CatalogNodeKind.Archive,
        DisplayName = $"{publicId}.cbz",
        RelativePath = $"Folder/{publicId}.cbz",
        PathKey = $"folder/{publicId}.cbz",
        SortKey = $"1{publicId}",
        Availability = (int)CatalogNodeAvailability.Available,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static ArchiveItemEntity Item(long nodeId) => new()
    {
        NodeId = nodeId,
        ArchiveFormat = 0,
        ByteLength = 1024,
        ModificationTicks = 0,
        ContentVersion = 1,
        AnalysisState = 0,
        PageCount = 10,
    };

    private sealed class RecordingObserver : IReadMarkObserver
    {
        private readonly ConcurrentQueue<ReadMarkChange> _changes = new();

        public IReadOnlyList<ReadMarkChange> Changes => _changes.ToArray();

        public void Clear() => _changes.Clear();

        public void OnReadMarksChanged(IReadOnlyList<ReadMarkChange> changes)
        {
            foreach (var change in changes)
                _changes.Enqueue(change);
        }
    }

    private sealed class ThrowingObserver : IReadMarkObserver
    {
        public void OnReadMarksChanged(IReadOnlyList<ReadMarkChange> changes) => throw new InvalidOperationException("observer failed");
    }
}
