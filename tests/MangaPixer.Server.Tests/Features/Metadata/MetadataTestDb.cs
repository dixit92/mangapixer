namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// A migrated, file-backed SQLite database plus builders for the metadata tests
/// (1.24.0). Synthetic names only.
/// </summary>
public sealed class MetadataTestDb : IAsyncDisposable
{
    private readonly string _dir;
    private int _seq;

    public MangaPixerDbContext Db { get; }
    public long LibraryId { get; private set; }
    public string LibraryPublicId { get; private set; } = string.Empty;
    public List<long> RemovedRecordIds { get; } = [];

    private MetadataTestDb(string dir, MangaPixerDbContext db)
    {
        _dir = dir;
        Db = db;
    }

    public static async Task<MetadataTestDb> CreateAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mangapixer-meta-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(dir, "meta.db")))
            .Options;
        var db = new MangaPixerDbContext(options);
        await db.Database.MigrateAsync();
        var t = new MetadataTestDb(dir, db);
        var lib = await t.AddLibraryAsync("metalib", "Meta Lib");
        t.LibraryId = lib.Id;
        t.LibraryPublicId = lib.PublicId;
        return t;
    }

    public async Task<LibraryEntity> AddLibraryAsync(string publicId, string name)
    {
        var lib = new LibraryEntity { PublicId = publicId, DisplayName = name, RootPath = "/synthetic/" + publicId, CreatedAt = DateTimeOffset.UtcNow };
        Db.Libraries.Add(lib);
        await Db.SaveChangesAsync();
        return lib;
    }

    public async Task<UserEntity> AddUserAsync(string name, bool isAdmin)
    {
        var user = new UserEntity
        {
            PublicId = "u-" + name,
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            IsActive = true,
            IsAdmin = isAdmin,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    public async Task<CatalogNodeEntity> AddFolderAsync(CatalogNodeEntity? parent, string name, long? libraryId = null) =>
        await AddNodeAsync(parent, name, CatalogNodeKind.Folder, libraryId);

    public async Task<CatalogNodeEntity> AddArchiveAsync(CatalogNodeEntity? parent, string name, long contentVersion = 1, int analysisState = 0, long? libraryId = null)
    {
        var node = await AddNodeAsync(parent, name, CatalogNodeKind.Archive, libraryId);
        Db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = node.Id, ContentVersion = contentVersion, AnalysisState = analysisState, PageCount = 3 });
        await Db.SaveChangesAsync();
        return node;
    }

    private async Task<CatalogNodeEntity> AddNodeAsync(CatalogNodeEntity? parent, string name, CatalogNodeKind kind, long? libraryId)
    {
        var n = ++_seq;
        var node = new CatalogNodeEntity
        {
            PublicId = $"n{n:D4}",
            LibraryId = libraryId ?? parent?.LibraryId ?? LibraryId,
            ParentId = parent?.Id,
            Kind = (int)kind,
            DisplayName = name,
            RelativePath = $"p{n}",
            PathKey = $"p{n}",
            SortKey = (kind == CatalogNodeKind.Folder ? "0" : "1") + name,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        Db.CatalogNodes.Add(node);
        await Db.SaveChangesAsync();
        return node;
    }

    /// <summary>Stores a parsed ComicInfo row for an archive (current content version unless given).</summary>
    public async Task AddComicInfoAsync(
        CatalogNodeEntity archive,
        string? series,
        string? number = null,
        int? year = null,
        int? volume = null,
        IReadOnlyList<ComicInfoCreator>? creators = null,
        IReadOnlyList<string>? genres = null,
        long? contentVersion = null,
        string? title = null,
        IReadOnlyList<string>? web = null)
    {
        var version = contentVersion ?? await Db.ArchiveItems.Where(a => a.NodeId == archive.Id).Select(a => a.ContentVersion).SingleAsync();
        await ComicInfoPersister.StageAsync(Db, archive.Id, version, new ComicInfoOutcome
        {
            Status = ComicInfoStatus.Parsed,
            Payload = new ComicInfoPayload
            {
                Series = series,
                Number = number,
                Year = year,
                Volume = volume,
                Title = title,
                Summary = title is null ? null : "Summary of " + title,
                Count = 10,
                Creators = creators ?? [],
                Genres = genres ?? [],
                WebUrls = web ?? [],
            },
        }, DateTimeOffset.UtcNow);
        await Db.SaveChangesAsync();
    }

    public async Task<MetadataRecordEntity> AddRecordAsync(
        string externalId,
        string title,
        string? genresJson = "[\"Action\",\"Drama\"]",
        string? description = "A synthetic web description.",
        int? startYear = 1999)
    {
        var record = new MetadataRecordEntity
        {
            PublicId = "r" + externalId,
            Provider = "mangaupdates",
            ExternalId = externalId,
            Title = title,
            Description = description,
            GenresJson = genresJson,
            CreatorsJson = "[{\"name\":\"Web Author\",\"role\":\"author\"}]",
            StartYear = startYear,
            Origin = (int)MetadataOrigin.Japan,
            Format = (int)MetadataFormat.Comic,
            Webtoon = false,
            OriginStatus = (int)MetadataOriginStatus.Ongoing,
            OriginVolumes = 12,
            SiteUrl = "https://www.mangaupdates.com/series/abc/synthetic",
            FetchedAt = DateTimeOffset.UtcNow,
        };
        Db.MetadataRecords.Add(record);
        await Db.SaveChangesAsync();
        return record;
    }

    public async Task AddLinkAsync(CatalogNodeEntity node, MetadataRecordEntity? record, SeriesLinkState state = SeriesLinkState.Confirmed)
    {
        Db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = node.Id,
            LibraryId = node.LibraryId,
            State = (int)state,
            RecordId = record?.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await Db.SaveChangesAsync();
    }

    public MetadataSettingsService Settings(IDictionary<string, string?>? config = null) => new(
        Db,
        new AuditService(Db),
        new ConfigurationBuilder().AddInMemoryCollection(config ?? new Dictionary<string, string?>()).Build(),
        TimeProvider.System,
        NullLogger<MetadataSettingsService>.Instance);

    public SeriesInfoResolver Resolver() => new(Db, Settings(), new MetadataProviderRegistry([]));

    public MetadataLinkService Links() => new(
        Db,
        new AuditService(Db),
        [new RecordingRemovedHandler(RemovedRecordIds)],
        TimeProvider.System,
        NullLogger<MetadataLinkService>.Instance);

    public async Task<Core.Api.SeriesInfoDto> ResolveAsync(CatalogNodeEntity node, bool includeItems = false)
    {
        Db.ChangeTracker.Clear();
        var fresh = await Db.CatalogNodes.AsNoTracking().SingleAsync(n => n.Id == node.Id);
        return await Resolver().ResolveAsync(fresh, includeItems);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private sealed class RecordingRemovedHandler(List<long> sink) : IMetadataRecordRemovedHandler
    {
        public Task OnRecordsRemovedAsync(IReadOnlyList<long> recordIds, CancellationToken ct)
        {
            sink.AddRange(recordIds);
            return Task.CompletedTask;
        }
    }
}
