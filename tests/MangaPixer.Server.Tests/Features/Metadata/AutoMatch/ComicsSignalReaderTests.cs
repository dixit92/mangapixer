namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of <see cref="ComicsSignalReader"/> (1.32.0): the stored ComicInfo publisher / imprint / web links /
/// notes / manga flag and the page counts of a work's archives become the detector input and the signal in
/// <see cref="MatchContext.Comics"/>. Synthetic names; a migrated SQLite database.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class ComicsSignalReaderTests : IAsyncLifetime
{
    private MetadataTestDb _db = null!;

    public async Task InitializeAsync() => _db = await MetadataTestDb.CreateAsync();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static MatchQuery Query(WorkClass cls = WorkClass.Series, DeclaredType? declared = null) => new(
        [new QueryVariant("Qzv Title", QueryVariantKind.Primary)],
        new MatchContext(cls, 3, 0, 0, null, null, false, [], DeclaredType: declared));

    private static FolderShape Shape(string folder, string? category = null) => new(folder, 1, [], [], null, category);

    private async Task<CatalogNodeEntity> ArchiveAsync(CatalogNodeEntity folder, string name, int pages)
    {
        var a = await _db.AddArchiveAsync(folder, name);
        await _db.Db.ArchiveItems.Where(i => i.NodeId == a.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.PageCount, pages));
        return a;
    }

    private async Task TagAsync(CatalogNodeEntity archive, string? publisher = null, string? imprint = null, string? notes = null,
        IReadOnlyList<string>? web = null, int? manga = null)
    {
        await ComicInfoPersister.StageAsync(_db.Db, archive.Id, 1, new ComicInfoOutcome
        {
            Status = ComicInfoStatus.Parsed,
            Payload = new ComicInfoPayload
            {
                Series = "Qzv Title",
                Publisher = publisher,
                Imprint = imprint,
                Notes = notes,
                WebUrls = web ?? [],
                MangaDirection = manga,
            },
        }, DateTimeOffset.UtcNow);
        await _db.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task ReadsTheMajorityPublisher_AnyNote_TheMedianPages_AndTheFolderName()
    {
        var folder = await _db.AddFolderAsync(null, "Qzv Title (2014)");
        var a1 = await ArchiveAsync(folder, "Qzv Title #001", 22);
        var a2 = await ArchiveAsync(folder, "Qzv Title #002", 26);
        var a3 = await ArchiveAsync(folder, "Qzv Title #003", 30);
        await TagAsync(a1, publisher: "Image");
        await TagAsync(a2, publisher: "image", notes: "Tagged with ComicTagger 1.5 using info from Comic Vine on 2023-01-01. [Issue ID 338526]");
        await TagAsync(a3, publisher: "Viz Media");
        _db.Db.ChangeTracker.Clear();

        var input = await ComicsSignalReader.ReadAsync(_db.Db, Query(), Shape(folder.DisplayName), [a1.Id, a2.Id, a3.Id], CancellationToken.None);

        Assert.Equal(["Qzv Title #001", "Qzv Title #002", "Qzv Title #003"], input.ArchiveNames.Order(StringComparer.Ordinal));
        Assert.Equal("image", input.ComicInfoPublisher, ignoreCase: true);
        Assert.Contains("[Issue ID 338526]", input.ComicInfoNotes);
        Assert.Equal(26, input.MedianPageCount);
        Assert.Equal("Qzv Title (2014)", input.FolderName);
        Assert.False(input.ComicInfoSaysManga);

        var query = await ComicsSignalReader.ApplyAsync(_db.Db, Query(), Shape(folder.DisplayName), [a1.Id, a2.Id, a3.Id], CancellationToken.None);
        var signal = query.Context.Comics!;
        Assert.Equal(ComicsSignalKind.WesternPublisher | ComicsSignalKind.ComicsIdInComicInfo | ComicsSignalKind.IssueNumbering
            | ComicsSignalKind.StartYearAfterName, signal.Kinds);
        Assert.Equal(ComicsPageShape.Issues, signal.PageShape);
        Assert.Equal(2014, signal.StartYear);
        Assert.Equal([new ComicsId(ComicsIdSite.ComicVine, ComicsIdKind.Issue, "338526")], signal.Ids);
    }

    [Fact]
    public async Task APublisherOnlyOneFileOfMany_Carries_IsNoMajority()
    {
        var folder = await _db.AddFolderAsync(null, "Qzv Title");
        var ids = new List<long>();
        for (var i = 1; i <= 3; i++)
        {
            var a = await ArchiveAsync(folder, $"Qzv Title v{i:D2}", 190);
            await TagAsync(a, publisher: i == 1 ? "Marvel" : "Kodansha");
            ids.Add(a.Id);
        }
        _db.Db.ChangeTracker.Clear();

        var query = await ComicsSignalReader.ApplyAsync(_db.Db, Query(), Shape(folder.DisplayName), ids, CancellationToken.None);

        Assert.Null(query.Context.Comics);
    }

    [Fact]
    public async Task ComicInfoSayingManga_SilencesTheWesternPublisher()
    {
        var folder = await _db.AddFolderAsync(null, "Qzv Title");
        var ids = new List<long>();
        for (var i = 1; i <= 2; i++)
        {
            var a = await ArchiveAsync(folder, $"Qzv Title v{i:D2}", 190);
            await TagAsync(a, publisher: "Fantagraphics", manga: 2);
            ids.Add(a.Id);
        }
        _db.Db.ChangeTracker.Clear();

        var input = await ComicsSignalReader.ReadAsync(_db.Db, Query(), Shape(folder.DisplayName), ids, CancellationToken.None);
        Assert.True(input.ComicInfoSaysManga);
        Assert.Same(ComicsSignal.None, ComicsSignals.Of(input));
    }

    [Fact]
    public async Task AnImprintNamingManga_CancelsThePublisher()
    {
        var folder = await _db.AddFolderAsync(null, "Qzv Title");
        var a = await ArchiveAsync(folder, "Qzv Title v01", 190);
        await TagAsync(a, publisher: "Panini Comics", imprint: "Planet Manga");
        _db.Db.ChangeTracker.Clear();

        var query = await ComicsSignalReader.ApplyAsync(_db.Db, Query(), Shape(folder.DisplayName), [a.Id], CancellationToken.None);

        Assert.Null(query.Context.Comics);
    }

    [Fact]
    public async Task AnArchiveLevelWork_IsNamedByItsArchives_NotByTheCollectionFolder()
    {
        var folder = await _db.AddFolderAsync(null, "Qzv One Shots (2010)");
        var a = await ArchiveAsync(folder, "Qzv Other Story", 40);
        _db.Db.ChangeTracker.Clear();

        var input = await ComicsSignalReader.ReadAsync(_db.Db, Query(WorkClass.CollectionLeaf), Shape(folder.DisplayName), [a.Id], CancellationToken.None);

        Assert.Null(input.FolderName);
        Assert.Equal(["Qzv Other Story"], input.ArchiveNames);
    }

    [Fact]
    public async Task ACategoryFolderAndAGcdLink_Route_WithoutComicInfoNames()
    {
        var folder = await _db.AddFolderAsync(null, "Qzv Album Series");
        var a = await ArchiveAsync(folder, "Qzv Album Series T01", 48);
        await TagAsync(a, web: ["https://www.comics.org/issue/1234/", "https://www.mangaupdates.com/series/abc/x"]);
        _db.Db.ChangeTracker.Clear();

        var query = await ComicsSignalReader.ApplyAsync(_db.Db, Query(), Shape(folder.DisplayName, "bandes dessinées"), [a.Id], CancellationToken.None);

        var signal = query.Context.Comics!;
        Assert.Equal(ComicsSignalKind.CategoryFolder | ComicsSignalKind.ComicsIdInComicInfo | ComicsSignalKind.AlbumNumbering, signal.Kinds);
        Assert.Equal([new ComicsId(ComicsIdSite.Gcd, ComicsIdKind.Issue, "1234")], signal.Ids);
    }

    [Fact]
    public async Task ADeclaredManga_ReadsNothingIntoTheContext()
    {
        var folder = await _db.AddFolderAsync(null, "Qzv Title (2014)");
        var a = await ArchiveAsync(folder, "Qzv Title #001", 22);
        await TagAsync(a, publisher: "Image");
        _db.Db.ChangeTracker.Clear();

        var query = await ComicsSignalReader.ApplyAsync(_db.Db, Query(declared: DeclaredType.Manga), Shape(folder.DisplayName), [a.Id], CancellationToken.None);

        Assert.Null(query.Context.Comics);
    }

    [Fact]
    public async Task NoArchives_FallsBackToTheShapesNames()
    {
        var shape = new FolderShape("Qzv Title", 1, ["Qzv Title Tome 1", "Qzv Title Tome 2"], [], null, "bd");

        var query = await ComicsSignalReader.ApplyAsync(_db.Db, Query(), shape, [], CancellationToken.None);

        Assert.Equal(ComicsSignalKind.CategoryFolder | ComicsSignalKind.AlbumNumbering, query.Context.Comics!.Kinds);
        Assert.Equal(ComicsPageShape.Unknown, query.Context.Comics.PageShape);
    }
}
