namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;

/// <summary>
/// Synthetic seeding for the Volumes view tests (1.29.0): a library, folders, archives, a linked record with a stored volume
/// map, ComicInfo volumes. Names are made up; nothing here reads a path or a real collection.
/// </summary>
internal static class VolumeTestData
{
    private static long s_next = 1000;

    private static string NextId() => "t" + Interlocked.Increment(ref s_next).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static async Task<LibraryEntity> AddLibraryAsync(MangaPixerDbContext db, string? publicId = null)
    {
        var library = new LibraryEntity
        {
            PublicId = publicId ?? NextId(),
            DisplayName = "Volumes Library",
            RootPath = "/synthetic/volumes",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return library;
    }

    public static async Task<CatalogNodeEntity> AddFolderAsync(MangaPixerDbContext db, long libraryId, long? parentId, string name, string? publicId = null) =>
        await AddNodeAsync(db, libraryId, parentId, CatalogNodeKind.Folder, name, publicId);

    public static async Task<CatalogNodeEntity> AddArchiveAsync(MangaPixerDbContext db, long libraryId, long? parentId, string name, string? publicId = null)
    {
        var node = await AddNodeAsync(db, libraryId, parentId, CatalogNodeKind.Archive, name, publicId);
        db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = node.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 12 });
        await db.SaveChangesAsync();
        return node;
    }

    private static async Task<CatalogNodeEntity> AddNodeAsync(
        MangaPixerDbContext db, long libraryId, long? parentId, CatalogNodeKind kind, string name, string? publicId)
    {
        var id = publicId ?? NextId();
        var node = new CatalogNodeEntity
        {
            PublicId = id,
            LibraryId = libraryId,
            ParentId = parentId,
            Kind = (int)kind,
            DisplayName = name,
            RelativePath = id,
            PathKey = id,
            SortKey = SortKey.ForNode(kind, name),
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node;
    }

    /// <summary>Archives named <c>{prefix}{n:000}</c> for every n in from..to.</summary>
    public static async Task AddChaptersAsync(MangaPixerDbContext db, long libraryId, long parentId, string prefix, int from, int to)
    {
        for (var n = from; n <= to; n++)
            await AddArchiveAsync(db, libraryId, parentId, $"{prefix}{n:000}");
    }

    public static async Task<MetadataRecordEntity> AddRecordAsync(
        MangaPixerDbContext db, string title = "Synthetic Record", int? originVolumes = null, MetadataOriginStatus status = MetadataOriginStatus.Ongoing)
    {
        var record = new MetadataRecordEntity
        {
            PublicId = NextId(),
            Provider = "mangaupdates",
            ExternalId = NextId(),
            Title = title,
            OriginVolumes = originVolumes,
            OriginStatus = (int)status,
            FetchedAt = DateTimeOffset.UtcNow,
        };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        return record;
    }

    public static async Task LinkAsync(MangaPixerDbContext db, CatalogNodeEntity folder, long? recordId, SeriesLinkState state = SeriesLinkState.Confirmed)
    {
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = folder.Id,
            LibraryId = folder.LibraryId,
            State = (int)state,
            RecordId = recordId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A stored MangaDex-style map: <c>(volume, first chapter, last chapter)</c> per volume.</summary>
    public static async Task<SeriesVolumeMapEntity> AddMapAsync(
        MangaPixerDbContext db, long recordId, int version = 1, int? knownVolumes = null, double? chaptersPerVolume = null,
        VolumeMapState state = VolumeMapState.Ok, VolumeMapSource source = VolumeMapSource.MangaDexAggregate,
        params (int Volume, int From, int To)[] volumes)
    {
        var json = "[" + string.Join(",", volumes.Select(v =>
            $"{{\"v\":\"{v.Volume}\",\"c\":[{string.Join(",", Enumerable.Range(v.From, v.To - v.From + 1).Select(c => $"\"{c}\""))}]}}")) + "]";
        var map = new SeriesVolumeMapEntity
        {
            RecordId = recordId,
            Source = (int)source,
            State = (int)state,
            VolumesJson = volumes.Length == 0 ? null : json,
            ChaptersPerVolume = chaptersPerVolume,
            KnownVolumeCount = knownVolumes,
            ContentHash = "h" + version,
            Version = version,
            FetchedAt = DateTimeOffset.UtcNow,
        };
        db.SeriesVolumeMaps.Add(map);
        await db.SaveChangesAsync();
        return map;
    }

    public static async Task AddComicInfoAsync(MangaPixerDbContext db, long nodeId, int? volume, string? number)
    {
        db.EmbeddedMetadata.Add(new EmbeddedMetadataEntity { NodeId = nodeId, Schema = 0, ContentVersion = 1, State = 1, Volume = volume, Number = number });
        await db.SaveChangesAsync();
    }

    public static async Task<UserEntity> AddUserAsync(MangaPixerDbContext db, string name = "admin", bool admin = true)
    {
        var user = new UserEntity
        {
            PublicId = NextId(),
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            IsActive = true,
            IsAdmin = admin,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
