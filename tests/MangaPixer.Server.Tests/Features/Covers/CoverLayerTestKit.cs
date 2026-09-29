namespace com.lifepixer.mangapixer.Tests.Server.Features.Covers;

using System.Globalization;
using System.Text;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Test kit of the cover layer (1.29.0): a migrated database (<see cref="MetadataTestDb"/>), a throwaway data root, and
/// fakes of the two worker seams. "Images" are STORED HASHES written into files (<c>HASH:&lt;hex&gt;</c>) - no picture, no
/// third-party art: the fake hasher reads the hash back, the fake renderer writes the hash a test assigned to a crop side.
/// </summary>
public sealed class CoverLayerTestKit : IAsyncDisposable
{
    private readonly string _root;

    private CoverLayerTestKit(MetadataTestDb db, string root)
    {
        Db = db;
        _root = root;
        DataRoot = Path.Combine(root, "data");
        LibraryRoot = Path.Combine(root, "library");
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LibraryRoot);
        Files = new CoverFiles(DataRoot);
        Thumbnails = new ThumbnailStore(Path.Combine(DataRoot, "thumbnails"));
        Posters = new MetadataImageStore(Path.Combine(DataRoot, "metadata-images"));
    }

    public static async Task<CoverLayerTestKit> CreateAsync()
    {
        var db = await MetadataTestDb.CreateAsync();
        var kit = new CoverLayerTestKit(db, Path.Combine(Path.GetTempPath(), "mangapixer-covers-" + Guid.NewGuid().ToString("N")[..8]));
        var library = await db.Db.Libraries.SingleAsync(l => l.Id == db.LibraryId);
        library.RootPath = kit.LibraryRoot;
        await db.Db.SaveChangesAsync();
        return kit;
    }

    public MetadataTestDb Db { get; }
    public string DataRoot { get; }
    public string LibraryRoot { get; }
    public CoverFiles Files { get; }
    public ThumbnailStore Thumbnails { get; }
    public MetadataImageStore Posters { get; }
    public FakeHasher Hasher { get; } = new();
    public FakeRenderer Renderer { get; } = new();

    public CoverDecisionService Decisions() => new(Db.Db, new CoverCropService(Db.Db, Renderer, Files), new CoverDirectionResolver(Db.Db),
        Hasher, Thumbnails, Posters, Files);

    public CoverResolutionService Resolutions() => new(Db.Db);

    public static string HashFile(ulong hash) => "HASH:" + hash.ToString("X16", CultureInfo.InvariantCulture);

    /// <summary>A ready archive: a source file under the library root, page 1 of the given size, and a file thumbnail with <paramref name="fileHash"/>.</summary>
    public async Task<CatalogNodeEntity> AddBookAsync(CatalogNodeEntity? parent, string name, int width, int height, ulong fileHash, long contentVersion = 1)
    {
        var node = await Db.AddArchiveAsync(parent, name, contentVersion);
        await File.WriteAllTextAsync(Path.Combine(LibraryRoot, node.RelativePath), "synthetic archive");
        var item = await Db.Db.ArchiveItems.SingleAsync(a => a.NodeId == node.Id);
        item.ModificationTicks = 42;
        item.ByteLength = 17;
        Db.Db.PageEntries.Add(new PageEntryEntity
        {
            ItemId = node.Id,
            ContentVersion = contentVersion,
            Ordinal = 0,
            EntryKey = "p0",
            SourceEntryLocator = "page001.png",
            MediaType = "image/png",
            Width = width,
            Height = height,
        });
        await Db.Db.SaveChangesAsync();
        await WriteThumbnailAsync(node.Id, contentVersion, fileHash);
        return node;
    }

    public async Task WriteThumbnailAsync(long nodeId, long contentVersion, ulong hash)
    {
        var temp = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(temp, HashFile(hash));
        await Thumbnails.PublishAsync(nodeId, contentVersion, temp);
        File.Delete(temp);
    }

    /// <summary>A MangaDex companion record for <paramref name="series"/> (as lane P will store it; synthetic rows here).</summary>
    public async Task<MetadataRecordEntity> AddCompanionAsync(MetadataRecordEntity series, MetadataOrigin origin = MetadataOrigin.Japan)
    {
        var companion = new MetadataRecordEntity
        {
            PublicId = "md" + series.ExternalId,
            Provider = "mangadex",
            ExternalId = "00000000-0000-0000-0000-" + series.ExternalId.PadLeft(12, '0'),
            Title = series.Title,
            Origin = (int)origin,
            FetchedAt = DateTimeOffset.UtcNow,
        };
        Db.Db.MetadataRecords.Add(companion);
        await Db.Db.SaveChangesAsync();
        Db.Db.MetadataCompanions.Add(new MetadataCompanionEntity
        {
            RecordId = series.Id,
            Provider = "mangadex",
            CompanionRecordId = companion.Id,
            State = (int)CompanionState.Auto,
            CheckedAt = DateTimeOffset.UtcNow,
        });
        await Db.Db.SaveChangesAsync();
        return companion;
    }

    private int _coverSeq;

    /// <summary>A stored web cover (row + file) with a stored hash.</summary>
    public async Task<VolumeCoverEntity> AddStoredCoverAsync(MetadataRecordEntity companion, int? volume, string locale, ulong hash,
        VolumeCoverKind kind = VolumeCoverKind.Volume)
    {
        var n = ++_coverSeq;
        var cover = new VolumeCoverEntity
        {
            PublicId = "vc" + n.ToString("x6", CultureInfo.InvariantCulture),
            ProviderRecordId = companion.Id,
            Kind = (int)kind,
            Volume = volume,
            Locale = locale,
            RemoteId = "remote-" + n,
            RemoteFile = "file-" + n + ".jpg",
            State = (int)VolumeCoverState.Stored,
            StoredVersion = 1,
            Hash = unchecked((long)hash),
            ListedAt = DateTimeOffset.UtcNow,
            StoredAt = DateTimeOffset.UtcNow,
        };
        Db.Db.VolumeCovers.Add(cover);
        await Db.Db.SaveChangesAsync();
        var path = Files.VolumeCoverPath(cover.PublicId, cover.StoredVersion);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, HashFile(hash));
        return cover;
    }

    /// <summary>Stores a poster for the record (PNG magic bytes, then the hash text).</summary>
    public async Task SetPosterAsync(MetadataRecordEntity record, ulong hash)
    {
        record.ImageState = 1;
        record.ImageVersion++;
        await Db.Db.SaveChangesAsync();
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Encoding.ASCII.GetBytes(HashFile(hash))).ToArray();
        await Posters.PublishAsync(record.Id, record.ImageVersion, bytes);
    }

    /// <summary>The app settings row (created with its defaults when missing).</summary>
    public async Task<AppSettingsEntity> SettingsAsync()
    {
        var row = await Db.Db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId);
        if (row is null)
        {
            row = new AppSettingsEntity { Id = AppSettingsEntity.SingletonId };
            Db.Db.AppSettings.Add(row);
            await Db.Db.SaveChangesAsync();
        }
        return row;
    }

    public async Task<NodeAutoCoverEntity?> AutoAsync(long nodeId) =>
        await Db.Db.NodeAutoCovers.AsNoTracking().FirstOrDefaultAsync(a => a.NodeId == nodeId);

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>Reads back the hash a test wrote into a file; counts the calls (the worker work the inputs key saves).</summary>
    public sealed class FakeHasher : ICoverHasher
    {
        public int Calls { get; private set; }

        public async Task<ulong?> HashFileAsync(string path, CancellationToken ct)
        {
            Calls++;
            var text = Encoding.ASCII.GetString(await File.ReadAllBytesAsync(path, ct));
            var at = text.IndexOf("HASH:", StringComparison.Ordinal);
            return at < 0 ? null : ulong.Parse(text.AsSpan(at + 5, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Writes the output file with the hash assigned to the requested crop side; records every request.</summary>
    public sealed class FakeRenderer : ICoverRenderer
    {
        public Dictionary<string, ulong> HashBySide { get; } = new(StringComparer.Ordinal)
        {
            [CoverCropSides.Left] = 0x1111_1111_1111_1111UL,
            [CoverCropSides.Right] = 0x2222_2222_2222_2222UL,
        };

        public List<CoverRenderRequest> Requests { get; } = [];

        public bool Fail { get; set; }

        public async Task<CoverRenderOutcome> RenderAsync(CoverRenderRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Fail)
                return CoverRenderOutcome.Failed(CoverRenderErrors.DecodeFailed);
            var hash = HashBySide.TryGetValue(request.CropSide, out var h) ? h : 0UL;
            await File.WriteAllTextAsync(request.OutputPath, HashFile(hash), ct);
            return CoverRenderOutcome.Ok(new CoverRenderResult
            {
                JobId = request.JobId,
                OutputPath = request.OutputPath,
                Width = 280,
                Height = 400,
                SourceWidth = 1400,
                SourceHeight = 1000,
                Hash = hash,
            });
        }
    }
}
