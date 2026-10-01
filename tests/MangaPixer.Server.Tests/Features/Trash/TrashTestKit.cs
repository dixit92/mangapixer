namespace com.lifepixer.mangapixer.Tests.Server.Features.Trash;

using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Features.Trash;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// A throwaway SQLite database + data root for the trash tests (1.31.0): synthetic libraries and nodes, tombstoned a chosen
/// number of days ago on a settable clock, with the stores the trash and Clean bundles touch.
/// </summary>
public sealed class TrashTestKit : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;
    private int _seq;

    public TrashTestKit()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-trash-" + Guid.NewGuid().ToString("N")[..8]);
        DataRoot = Path.Combine(_tempDir, "data");
        Directory.CreateDirectory(DataRoot);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "test.db")))
            .Options;
        Thumbnails = new ThumbnailStore(Path.Combine(DataRoot, "thumbnails"));
        CoverFiles = new CoverFiles(DataRoot);
        VolumeCovers = new VolumeCoverStore(Path.Combine(DataRoot, VolumeCoverStore.FolderName));
        Images = new MetadataImageStore(Path.Combine(DataRoot, "metadata-images"));
    }

    public string DataRoot { get; }
    public ManualTime Clock { get; } = new(Now);
    public ThumbnailStore Thumbnails { get; }
    public CoverFiles CoverFiles { get; }
    public VolumeCoverStore VolumeCovers { get; }
    public MetadataImageStore Images { get; }
    public long UserId { get; private set; }

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
        var user = new UserEntity
        {
            PublicId = "u1",
            UserName = "reader",
            NormalizedUserName = "READER",
            PasswordHash = "hash",
            SecurityStamp = "stamp",
            CreatedAt = Now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        UserId = user.Id;
    }

    public async Task<long> AddLibraryAsync(string publicId)
    {
        await using var db = NewContext();
        var library = new LibraryEntity { PublicId = publicId, DisplayName = "Library " + publicId, RootPath = "/synthetic/" + publicId, CreatedAt = Now };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return library.Id;
    }

    /// <summary>Adds a node; <paramref name="tombstonedDaysAgo"/> null = present. An archive gets an analysed item at content version 2 with a thumbnail of it.</summary>
    public async Task<long> AddNodeAsync(long libraryId, long? parentId, int kind, double? tombstonedDaysAgo)
    {
        await using var db = NewContext();
        var n = ++_seq;
        var node = new CatalogNodeEntity
        {
            PublicId = "n" + n,
            LibraryId = libraryId,
            ParentId = parentId,
            Kind = kind,
            DisplayName = (kind == 1 ? "Item " : "Folder ") + n,
            RelativePath = "p" + n,
            PathKey = "p" + n,
            SortKey = "s" + n,
            Availability = tombstonedDaysAgo is null ? 0 : 5,
            TombstonedAt = tombstonedDaysAgo is { } d ? Now - TimeSpan.FromDays(d) : null,
            CreatedAt = Now - TimeSpan.FromDays(400),
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        if (kind == 1)
        {
            db.ArchiveItems.Add(new ArchiveItemEntity
            {
                NodeId = node.Id,
                ContentVersion = 2,
                AnalysisState = 0,
                PageCount = 1,
                ThumbnailState = 1,
                ThumbnailContentVersion = 2,
            });
            db.PageEntries.Add(new PageEntryEntity
            {
                ItemId = node.Id,
                ContentVersion = 2,
                Ordinal = 0,
                EntryKey = "p0",
                SourceEntryLocator = "loc",
                MediaType = "image/webp",
                ByteSize = 10,
            });
            await db.SaveChangesAsync();
        }
        return node.Id;
    }

    /// <summary>The user's reading progress, read mark, bookmark, reader override and favorite on an item.</summary>
    public async Task AddUserStateAsync(long itemId)
    {
        await using var db = NewContext();
        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = UserId,
            ItemId = itemId,
            ContentVersion = 2,
            EntryKey = "p0",
            Ordinal = 0,
            State = 1,
            Revision = 1,
            UpdatedAt = Now,
        });
        db.ReadMarks.Add(new ReadMarkEntity { UserId = UserId, ItemId = itemId, MarkedAt = Now, Source = "manual" });
        db.Bookmarks.Add(new BookmarkEntity
        {
            UserId = UserId,
            ItemId = itemId,
            ContentVersion = 2,
            EntryKey = "p0",
            Ordinal = 0,
            NormalizedAnchor = 0.5,
            CreatedAt = Now,
        });
        db.ItemReaderOverrides.Add(new ItemReaderOverridesEntity { UserId = UserId, ItemId = itemId, ReaderMode = 2 });
        db.Favorites.Add(new FavoriteEntity { UserId = UserId, CatalogNodeId = itemId, CreatedAt = Now });
        await db.SaveChangesAsync();
    }

    /// <summary>Writes a file with a chosen age (last write time).</summary>
    public static string WriteFile(string path, int bytes = 16, TimeSpan? age = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, (Now - (age ?? TimeSpan.FromDays(3))).UtcDateTime);
        return path;
    }

    /// <summary>The thumbnail and both cover crops of an archive at content version 2.</summary>
    public (string Thumbnail, string Left, string Right) WriteArchiveFiles(long archiveId) => (
        WriteFile(Thumbnails.GetThumbnailPath(archiveId, 2), 100),
        WriteFile(CoverFiles.CropPath(archiveId, 2, com.lifepixer.mangapixer.Core.Metadata.CoverCropSide.Left), 50),
        WriteFile(CoverFiles.CropPath(archiveId, 2, com.lifepixer.mangapixer.Core.Metadata.CoverCropSide.Right), 50));

    public async Task SetRetentionAsync(int? days, bool automatic = false)
    {
        await using var db = NewContext();
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId);
        if (row is null)
        {
            row = new AppSettingsEntity { Id = AppSettingsEntity.SingletonId };
            db.AppSettings.Add(row);
        }
        row.TrashRetentionDays = days;
        row.TrashAutoCleanEnabled = automatic;
        await db.SaveChangesAsync();
    }

    public async Task AddScanRunAsync(long libraryId, int status, string? error)
    {
        await using var db = NewContext();
        db.ScanRuns.Add(new ScanRunEntity { LibraryId = libraryId, ScanRevision = 1, Status = status, SanitizedError = error, StartedAt = Now });
        await db.SaveChangesAsync();
    }

    public TrashService Service(MangaPixerDbContext db, ITombstoneHolds? holds = null)
    {
        var purger = new NodePurger(db, Thumbnails, CoverFiles);
        var bundles = new BundleCleaner(db, Clock, Thumbnails, CoverFiles, VolumeCovers, Images);
        return new TrashService(db, holds ?? new NoTombstoneHolds(db), purger, bundles, new AuditService(db), new TrashRunGate(), Clock,
            NullLogger<TrashService>.Instance);
    }

    public BundleCleaner Bundles(MangaPixerDbContext db) => new(db, Clock, Thumbnails, CoverFiles, VolumeCovers, Images);

    public async Task<bool> ExistsAsync(long nodeId)
    {
        await using var db = NewContext();
        return await db.CatalogNodes.AnyAsync(n => n.Id == nodeId);
    }
}

/// <summary>Holds a fixed set of tombstones (what move recognition does for an open conflict).</summary>
public sealed class FixedTombstoneHolds(MangaPixerDbContext db, params long[] ids) : ITombstoneHolds
{
    public IQueryable<long> HeldNodeIds() => db.CatalogNodes.Where(n => ids.Contains(n.Id)).Select(n => n.Id);
}
