namespace com.lifepixer.mangapixer.Tests.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Library.Moves;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Two (or more) libraries on writable temp folders over one SQLite file, for cross-library move tests (1.31.0). Scans run on
/// a fresh context and record a completed scan run as the launcher does; <see cref="AnalyseAsync"/> stands in for the worker
/// (signature from the bytes on disk + a synthetic manifest whose entries derive from the file's bytes). Archives are random
/// bytes (synthetic), so equal seeds mean byte-identical copies.
/// </summary>
public sealed class MoveTestHarness : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;
    private readonly Dictionary<long, long> _revisions = [];

    public Dictionary<long, string> Roots { get; } = [];

    public MoveTestHarness()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-moves-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "test.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    public MangaPixerDbContext NewContext() => new(_options);

    public async Task InitAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        db.AppSettings.Add(new AppSettingsEntity { Id = AppSettingsEntity.SingletonId });
        await db.SaveChangesAsync();
    }

    public async Task<long> AddLibraryAsync(string name)
    {
        var root = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(root);
        await using var db = NewContext();
        var library = new LibraryEntity { PublicId = "lib" + name, DisplayName = name, RootPath = root, CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        Roots[library.Id] = root;
        return library.Id;
    }

    public async Task<long> AddUserAsync(string name, bool admin = false)
    {
        await using var db = NewContext();
        var user = new UserEntity
        {
            PublicId = "u" + name,
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            PasswordHash = "x",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            IsAdmin = admin,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Scans a library at its next revision and records the completed run (what the launcher writes).</summary>
    public async Task<ScanResult> ScanAsync(long libraryId)
    {
        var revision = _revisions[libraryId] = _revisions.GetValueOrDefault(libraryId) + 1;
        var started = DateTimeOffset.UtcNow;
        ScanResult result;
        await using (var db = NewContext())
        {
            var coordinator = new LibraryScanCoordinator(db, new ReadOnlyLibraryFileSystem(Roots[libraryId]), new LibraryScanPolicy(),
                libraryId, revision, "test");
            result = await coordinator.ScanAsync();
        }
        await using (var db = NewContext())
        {
            db.ScanRuns.Add(new ScanRunEntity
            {
                LibraryId = libraryId,
                ScanRevision = revision,
                Status = 2,
                StartedAt = started,
                CompletedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        // Distinct timestamps between consecutive steps (CreatedAt vs a run's completion).
        await Task.Delay(15);
        return result;
    }

    public string PathOf(long libraryId, string relative) => Path.Combine(Roots[libraryId], relative.Replace('/', Path.DirectorySeparatorChar));

    public void WriteArchive(long libraryId, string relative, int seed, int size = 4096)
    {
        var path = PathOf(libraryId, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
    }

    /// <summary>Moves a file or folder between libraries (or within one) on disk.</summary>
    public void Move(long fromLibrary, string fromRelative, long toLibrary, string toRelative)
    {
        var from = PathOf(fromLibrary, fromRelative);
        var to = PathOf(toLibrary, toRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }

    public void Copy(long fromLibrary, string fromRelative, long toLibrary, string toRelative)
    {
        var to = PathOf(toLibrary, toRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(PathOf(fromLibrary, fromRelative), to);
    }

    /// <summary>
    /// What a successful analysis persists: signature, page count, a manifest. Page entries are derived from the file's bytes
    /// (identical files give identical manifests); <paramref name="pages"/> pages, entry names <c>pNN.jpg</c>.
    /// </summary>
    public async Task AnalyseAsync(long nodeId, int pages = 4, bool reversedOrder = false)
    {
        await using var db = NewContext();
        var node = await db.CatalogNodes.Include(n => n.ArchiveItem).SingleAsync(n => n.Id == nodeId);
        var root = Roots[node.LibraryId];
        var path = Path.Combine(root, node.RelativePath);
        var item = node.ArchiveItem!;
        item.ContentSignature = ContentSignature.TryComputeFile(path);
        item.AnalysisState = 0;
        item.PageCount = pages;
        await db.PageEntries.Where(p => p.ItemId == nodeId).ExecuteDeleteAsync();
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i < pages; i++)
        {
            var entry = reversedOrder ? pages - 1 - i : i;
            db.PageEntries.Add(new PageEntryEntity
            {
                ItemId = nodeId,
                ContentVersion = item.ContentVersion,
                Ordinal = i,
                EntryKey = new PageEntryKey(i).ToOpaque(),
                SourceEntryLocator = $"p{entry:00}.jpg",
                MediaType = "image/jpeg",
                ByteSize = 1000 + bytes[entry % bytes.Length] + entry,
            });
        }
        await db.SaveChangesAsync();
    }

    public async Task AnalyseLibraryAsync(long libraryId, int pages = 4)
    {
        List<long> ids;
        await using (var db = NewContext())
            ids = await db.CatalogNodes.Where(n => n.LibraryId == libraryId && n.Kind == 1 && n.Availability != 5).Select(n => n.Id).ToListAsync();
        foreach (var id in ids)
            await AnalyseAsync(id, pages);
    }

    public async Task<CatalogNodeEntity> NodeAsync(long libraryId, string pathKey)
    {
        await using var db = NewContext();
        return await db.CatalogNodes.AsNoTracking().SingleAsync(n => n.LibraryId == libraryId && n.PathKey == pathKey);
    }

    public async Task<CatalogNodeEntity?> FindAsync(long libraryId, string pathKey)
    {
        await using var db = NewContext();
        return await db.CatalogNodes.AsNoTracking().SingleOrDefaultAsync(n => n.LibraryId == libraryId && n.PathKey == pathKey);
    }

    public async Task<CatalogNodeEntity> NodeByIdAsync(long id)
    {
        await using var db = NewContext();
        return await db.CatalogNodes.AsNoTracking().SingleAsync(n => n.Id == id);
    }

    public async Task SetProgressAsync(long userId, long itemId, int ordinal, int state = 1)
    {
        await using var db = NewContext();
        var cv = await db.ArchiveItems.Where(a => a.NodeId == itemId).Select(a => a.ContentVersion).SingleAsync();
        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = userId,
            ItemId = itemId,
            ContentVersion = cv,
            Ordinal = ordinal,
            EntryKey = new PageEntryKey(ordinal).ToOpaque(),
            State = state,
            Revision = 1,
            LastMutationId = Guid.NewGuid().ToString("N"),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public async Task MarkReadAsync(long userId, long itemId)
    {
        await using var db = NewContext();
        db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = itemId, MarkedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    public async Task FavoriteAsync(long userId, long nodeId)
    {
        await using var db = NewContext();
        db.Favorites.Add(new FavoriteEntity { UserId = userId, CatalogNodeId = nodeId, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    /// <summary>Back-dates a tombstone (window tests).</summary>
    public async Task SetTombstonedAtAsync(long nodeId, DateTimeOffset at)
    {
        await using var db = NewContext();
        await db.CatalogNodes.Where(n => n.Id == nodeId).ExecuteUpdateAsync(u => u.SetProperty(n => n.TombstonedAt, at));
    }

    public async Task SetRetentionAsync(int days)
    {
        await using var db = NewContext();
        await db.AppSettings.ExecuteUpdateAsync(u => u.SetProperty(s => s.TrashRetentionDays, days));
    }

    public static int Tombstoned => (int)CatalogNodeAvailability.Tombstoned;

    public static MetadataCarryOverService CarryOver(MangaPixerDbContext db) =>
        new(db, new AuditService(db), TimeProvider.System, [], NullLogger<MetadataCarryOverService>.Instance);

    public static MovePairingService Pairing(MangaPixerDbContext db) =>
        new(db, CarryOver(db), TimeProvider.System, NullLogger<MovePairingService>.Instance);

    public static MoveConflictService Conflicts(MangaPixerDbContext db) =>
        new(db, CarryOver(db), new AuditService(db), TimeProvider.System, NullLogger<MoveConflictService>.Instance);

    /// <summary>Runs one pairing pass on a fresh context.</summary>
    public async Task<MovePairingService.PassResult> PairAsync()
    {
        await using var db = NewContext();
        return await Pairing(db).RunAsync();
    }

    public async Task<long> AddRecordAsync(string externalId, string title)
    {
        await using var db = NewContext();
        var record = new MetadataRecordEntity { PublicId = "r" + externalId, Provider = "mangaupdates", ExternalId = externalId, Title = title, FetchedAt = DateTimeOffset.UtcNow };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        return record.Id;
    }

    public async Task LinkAsync(long nodeId, SeriesLinkState state, long? recordId)
    {
        await using var db = NewContext();
        var libraryId = await db.CatalogNodes.Where(n => n.Id == nodeId).Select(n => n.LibraryId).SingleAsync();
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = nodeId,
            LibraryId = libraryId,
            State = (int)state,
            RecordId = recordId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public async Task<NodeSeriesLinkEntity?> LinkOfAsync(long nodeId)
    {
        await using var db = NewContext();
        return await db.NodeSeriesLinks.AsNoTracking().SingleOrDefaultAsync(l => l.NodeId == nodeId);
    }

    public async Task<ReadingProgressEntity?> ProgressAsync(long userId, long itemId)
    {
        await using var db = NewContext();
        return await db.ReadingProgress.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId && p.ItemId == itemId);
    }

    public async Task<int> CountAsync(Func<MangaPixerDbContext, Task<int>> query)
    {
        await using var db = NewContext();
        return await query(db);
    }
}
