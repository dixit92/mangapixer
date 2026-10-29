namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of the archive context line helper (1.50.0, lane W): the nearest ancestor folder that is not a plain
/// unit / kind folder, resolved in ONE upward query for a batch, limited to the viewer's visible libraries. Real file-backed SQLite.
/// </summary>
public sealed class ArchiveContextNamesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;
    private int _counter;

    public ArchiveContextNamesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-ctx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "ctx.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<MangaPixerDbContext> NewDbAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        return db;
    }

    private static async Task<long> AddLibraryAsync(MangaPixerDbContext db, string name)
    {
        var lib = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
            DisplayName = name,
            RootPath = "/synthetic/" + name,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        return lib.Id;
    }

    private async Task<long> AddAsync(MangaPixerDbContext db, long libraryId, CatalogNodeKind kind, string name, long? parentId = null)
    {
        var n = ++_counter;
        var node = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(1000 + n),
            LibraryId = libraryId,
            ParentId = parentId,
            Kind = (int)kind,
            DisplayName = name,
            RelativePath = "/synthetic/n" + n,
            PathKey = "/synthetic/n" + n,
            SortKey = (kind == CatalogNodeKind.Folder ? "0" : "1") + name,
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node.Id;
    }

    private static Task<long> FolderAsync(ArchiveContextNamesTests t, MangaPixerDbContext db, long lib, string name, long? parent = null) =>
        t.AddAsync(db, lib, CatalogNodeKind.Folder, name, parent);

    [Fact]
    public async Task SeriesFolderDirectlyAbove_IsTheContext()
    {
        await using var db = await NewDbAsync();
        var lib = await AddLibraryAsync(db, "A");
        var series = await FolderAsync(this, db, lib, "Berserk");
        var vol = await AddAsync(db, lib, CatalogNodeKind.Archive, "Vol. 012", series);

        var contexts = await ArchiveContextNames.ResolveAsync(db, [vol], [lib]);

        Assert.Equal("Berserk", Assert.Single(contexts).Value);
        Assert.Equal("Berserk", await ArchiveContextNames.ResolveAsync(db, vol, [lib]));
    }

    [Theory]
    [InlineData("Volumes")]
    [InlineData("Chapters")]
    [InlineData("Extras")]
    [InlineData("Vol 3")]
    [InlineData("Vol. 1-5")]
    [InlineData("Ch 1-50")]
    [InlineData("Season 2")]
    [InlineData("Part 3")]
    [InlineData("12")]
    [InlineData("Side Stories")]
    [InlineData("Omake")]
    public async Task KindFolders_AreSkipped(string kindFolder)
    {
        await using var db = await NewDbAsync();
        var lib = await AddLibraryAsync(db, "A");
        var series = await FolderAsync(this, db, lib, "Some Series");
        var kind = await FolderAsync(this, db, lib, kindFolder, series);
        var archive = await AddAsync(db, lib, CatalogNodeKind.Archive, "Ch. 0106.00 Vol. 006", kind);

        var contexts = await ArchiveContextNames.ResolveAsync(db, [archive], [lib]);

        Assert.Equal("Some Series", contexts[archive]);
    }

    [Theory]
    [InlineData("Volume Control")]
    [InlineData("Chapter Zero")]
    [InlineData("Part 3 - Phantom Blood")]
    [InlineData("Extras Club")]
    public async Task ATitleThatMerelyStartsLikeAKindFolder_IsAContext(string title)
    {
        await using var db = await NewDbAsync();
        var lib = await AddLibraryAsync(db, "A");
        var folder = await FolderAsync(this, db, lib, title);
        var archive = await AddAsync(db, lib, CatalogNodeKind.Archive, "Vol. 001", folder);

        Assert.Equal(title, (await ArchiveContextNames.ResolveAsync(db, [archive], [lib]))[archive]);
    }

    [Fact]
    public async Task NestedLayouts_UseTheNearestNonKindAncestor()
    {
        await using var db = await NewDbAsync();
        var lib = await AddLibraryAsync(db, "A");
        // Manga / Series / Volumes / Extras / Vol 1 / file.cbz  -> Series
        var manga = await FolderAsync(this, db, lib, "Manga");
        var series = await FolderAsync(this, db, lib, "Series", manga);
        var volumes = await FolderAsync(this, db, lib, "Volumes", series);
        var extras = await FolderAsync(this, db, lib, "Extras", volumes);
        var vol1 = await FolderAsync(this, db, lib, "Vol 1", extras);
        var deep = await AddAsync(db, lib, CatalogNodeKind.Archive, "file", vol1);
        // Artist / Series A / Chapters / file ; Artist / Series B / file  -> each its own series folder, not the artist
        var artist = await FolderAsync(this, db, lib, "Some Artist");
        var seriesA = await FolderAsync(this, db, lib, "Series A", artist);
        var chaptersA = await FolderAsync(this, db, lib, "Chapters", seriesA);
        var a = await AddAsync(db, lib, CatalogNodeKind.Archive, "Ch. 0001", chaptersA);
        var seriesB = await FolderAsync(this, db, lib, "Series B", artist);
        var b = await AddAsync(db, lib, CatalogNodeKind.Archive, "Vol. 001", seriesB);
        // an archive straight in a category-like folder: the category word is a name like any other (no invented list)
        var loose = await AddAsync(db, lib, CatalogNodeKind.Archive, "One-shot", manga);

        var contexts = await ArchiveContextNames.ResolveAsync(db, [deep, a, b, loose], [lib]);

        Assert.Equal("Series", contexts[deep]);
        Assert.Equal("Series A", contexts[a]);
        Assert.Equal("Series B", contexts[b]);
        Assert.Equal("Manga", contexts[loose]);
    }

    [Fact]
    public async Task RootArchives_Folders_AndKindFolderOnlyChains_HaveNoContext()
    {
        await using var db = await NewDbAsync();
        var lib = await AddLibraryAsync(db, "A");
        var rootArchive = await AddAsync(db, lib, CatalogNodeKind.Archive, "Loose");
        var folder = await FolderAsync(this, db, lib, "Series");
        var volumes = await FolderAsync(this, db, lib, "Volumes");
        var underKindOnly = await AddAsync(db, lib, CatalogNodeKind.Archive, "Vol 1", volumes);

        var contexts = await ArchiveContextNames.ResolveAsync(db, [rootArchive, folder, volumes, underKindOnly, 987654321], [lib]);

        Assert.Empty(contexts);
    }

    [Fact]
    public async Task OnlyVisibleLibraries_AreAnswered()
    {
        await using var db = await NewDbAsync();
        var visible = await AddLibraryAsync(db, "Visible");
        var hidden = await AddLibraryAsync(db, "Hidden");
        var seriesV = await FolderAsync(this, db, visible, "Seen Series");
        var archiveV = await AddAsync(db, visible, CatalogNodeKind.Archive, "Vol. 001", seriesV);
        var seriesH = await FolderAsync(this, db, hidden, "Secret Series");
        var archiveH = await AddAsync(db, hidden, CatalogNodeKind.Archive, "Vol. 001", seriesH);

        var contexts = await ArchiveContextNames.ResolveAsync(db, [archiveV, archiveH], [visible]);

        Assert.Equal(new Dictionary<long, string> { [archiveV] = "Seen Series" }, contexts);
        Assert.Empty(await ArchiveContextNames.ResolveAsync(db, [archiveV, archiveH], []));
    }

    [Fact]
    public async Task ABatch_IsOneQuery_AndLargeBatchesAreChunked()
    {
        await using var db = await NewDbAsync();
        var lib = await AddLibraryAsync(db, "A");
        var series = await FolderAsync(this, db, lib, "Series");
        var real = new List<long>();
        for (var i = 0; i < 3; i++)
            real.Add(await AddAsync(db, lib, CatalogNodeKind.Archive, "Vol. 00" + i, series));
        // More ids than one statement carries: the real ones sit in different chunks.
        var ids = new List<long> { real[0] };
        ids.AddRange(Enumerable.Range(1_000_000, ArchiveContextNames.ChunkSize + 10).Select(i => (long)i));
        ids.Add(real[1]);
        ids.AddRange(Enumerable.Range(2_000_000, ArchiveContextNames.ChunkSize).Select(i => (long)i));
        ids.Add(real[2]);
        ids.Add(real[0]); // a duplicate

        var contexts = await ArchiveContextNames.ResolveAsync(db, ids, [lib]);

        Assert.Equal(3, contexts.Count);
        Assert.All(real, id => Assert.Equal("Series", contexts[id]));
    }

    [Fact]
    public async Task TheNameIsTrimmed_AndNeverAPath()
    {
        await using var db = await NewDbAsync();
        var lib = await AddLibraryAsync(db, "A");
        var series = await FolderAsync(this, db, lib, "  Padded Series ");
        var archive = await AddAsync(db, lib, CatalogNodeKind.Archive, "Vol. 001", series);

        var name = (await ArchiveContextNames.ResolveAsync(db, [archive], [lib]))[archive];

        Assert.Equal("Padded Series", name);
        Assert.DoesNotContain('/', name);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Volumes", false)]
    [InlineData("Berserk", true)]
    public void IsContextName_FollowsTheKindFolderRule(string? name, bool expected) =>
        Assert.Equal(expected, ArchiveContextNames.IsContextName(name));
}
