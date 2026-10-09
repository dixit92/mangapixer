namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Declared;

using System.Data.Common;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Authors;
using com.lifepixer.mangapixer.Server.Features.Metadata.Declared;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests of declared facts (1.28.0): the bulk reader lane M consumes (nearest wins per key,
/// library scope, deleted and tombstoned folders, two queries per library), the admin edits (validation,
/// order, dedupe, other keys untouched, folders only) and the Info-panel view (inheritance to archives,
/// conflicts with a linked record, "Show series information" off). Synthetic names only.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class DeclaredFactsServiceTests : IAsyncLifetime
{
    private MetadataTestDb _t = null!;

    public async Task InitializeAsync() => _t = await MetadataTestDb.CreateAsync();

    public async Task DisposeAsync() => await _t.DisposeAsync();

    private DeclaredFactsService Service() => new(
        _t.Db,
        new AuditService(_t.Db),
        _t.Settings(),
        _t.Resolver(),
        new MetadataProviderRegistry([]),
        NullLogger<DeclaredFactsService>.Instance,
        TimeProvider.System,
        new StoredAuthorAliases(_t.Db));

    private DeclaredFactsReader Reader() => new(_t.Db);

    private static SetDeclaredFactsRequest Declare(DeclaredType? type, params (string Name, string? Role)[] creators) => new()
    {
        Type = type,
        Creators = creators.Select(c => new DeclaredCreatorDto { Name = c.Name, Role = c.Role }).ToList(),
    };

    private async Task SetFolderAsync(CatalogNodeEntity folder, SetDeclaredFactsRequest request)
    {
        var result = await Service().SetFolderAsync(folder.PublicId, request, "admin");
        Assert.Equal(MetadataLinkResultCode.Ok, result.Code);
    }

    private async Task SetLibraryAsync(SetDeclaredFactsRequest request, string? libraryPublicId = null)
    {
        var result = await Service().SetLibraryAsync(libraryPublicId ?? _t.LibraryPublicId, request, "admin");
        Assert.Equal(MetadataLinkResultCode.Ok, result.Code);
    }

    // --- IDeclaredFactsReader (lane M's contract) ---

    [Fact]
    public async Task Reader_NearestWinsPerKey_AcrossFolderAndLibraryScopes()
    {
        var a = await _t.AddFolderAsync(null, "Shelf A");
        var b = await _t.AddFolderAsync(a, "Series B");
        var c = await _t.AddFolderAsync(b, "Volumes");
        var d = await _t.AddFolderAsync(null, "Shelf D");
        var archive = await _t.AddArchiveAsync(b, "Series B v01");

        await SetLibraryAsync(Declare(DeclaredType.Manga, ("Library Author", null)));
        await SetFolderAsync(a, Declare(DeclaredType.Manhwa));
        await SetFolderAsync(b, Declare(null, ("Folder Writer", "writer"), ("Folder Artist", "artist")));

        var facts = await Reader().EffectiveForLibraryAsync(_t.LibraryId, CancellationToken.None);

        // Type from A (own), creators from the library.
        Assert.Equal("manhwa", facts[a.Id].Type);
        Assert.Equal(DeclaredFactSource.Own, facts[a.Id].TypeSource);
        Assert.Equal(["Library Author"], facts[a.Id].Creators.Select(x => x.Name));
        Assert.Equal(DeclaredFactSource.Library, facts[a.Id].CreatorsSource);

        // B: type inherited from A; its own creator list REPLACES the library's (lists are never merged).
        Assert.Equal("manhwa", facts[b.Id].Type);
        Assert.Equal(DeclaredFactSource.Inherited, facts[b.Id].TypeSource);
        Assert.Equal([new DeclaredCreator("Folder Writer", "writer"), new DeclaredCreator("Folder Artist", "artist")], facts[b.Id].Creators);
        Assert.Equal(DeclaredFactSource.Own, facts[b.Id].CreatorsSource);

        // C inherits both from above; D (a sibling shelf) only has the library's.
        Assert.Equal(DeclaredType.Manhwa, facts[c.Id].TypeValue);
        Assert.Equal(DeclaredFactSource.Inherited, facts[c.Id].TypeSource);
        Assert.Equal(DeclaredFactSource.Inherited, facts[c.Id].CreatorsSource);
        Assert.Equal("Folder Writer", facts[c.Id].Creators[0].Name);
        Assert.Equal("manga", facts[d.Id].Type);
        Assert.Equal(DeclaredFactSource.Library, facts[d.Id].TypeSource);

        // Folders only: archives are never keys.
        Assert.False(facts.ContainsKey(archive.Id));
        Assert.Equal(4, facts.Count);
    }

    [Fact]
    public async Task Reader_NothingDeclared_IsEmpty_AndOtherLibrariesAreNotAffected()
    {
        var a = await _t.AddFolderAsync(null, "Shelf A");
        var other = await _t.AddLibraryAsync("otherlib", "Other");
        var o = await _t.AddFolderAsync(null, "Other Shelf", other.Id);

        Assert.Empty(await Reader().EffectiveForLibraryAsync(_t.LibraryId, CancellationToken.None));

        await SetLibraryAsync(Declare(DeclaredType.Webtoon), "otherlib");
        Assert.Empty(await Reader().EffectiveForLibraryAsync(_t.LibraryId, CancellationToken.None));
        var otherFacts = await Reader().EffectiveForLibraryAsync(other.Id, CancellationToken.None);
        Assert.Equal("webtoon", Assert.Single(otherFacts).Value.Type);
        Assert.Equal(o.Id, otherFacts.Single().Key);
        Assert.NotEqual(a.Id, otherFacts.Single().Key);
    }

    [Fact]
    public async Task Reader_OnlyFoldersBelowAFolderDeclaration_AreListed()
    {
        var a = await _t.AddFolderAsync(null, "Shelf A");
        var b = await _t.AddFolderAsync(a, "Series B");
        var d = await _t.AddFolderAsync(null, "Shelf D");
        await SetFolderAsync(b, Declare(DeclaredType.Novel));

        var facts = await Reader().EffectiveForLibraryAsync(_t.LibraryId, CancellationToken.None);

        Assert.Equal([b.Id], facts.Keys);
        Assert.False(facts.ContainsKey(a.Id));
        Assert.False(facts.ContainsKey(d.Id));
    }

    [Fact]
    public async Task Reader_DeletedFolder_CascadesItsFacts_TombstonedFolderIsSkipped()
    {
        var a = await _t.AddFolderAsync(null, "Shelf A");
        var b = await _t.AddFolderAsync(a, "Series B");
        var gone = await _t.AddFolderAsync(null, "Removed");
        var ghost = await _t.AddFolderAsync(null, "Tombstoned");
        var below = await _t.AddFolderAsync(ghost, "Below Tombstoned");
        await SetFolderAsync(a, Declare(DeclaredType.Manhua));
        await SetFolderAsync(gone, Declare(DeclaredType.Comic, ("Gone Author", null)));
        await SetFolderAsync(ghost, Declare(DeclaredType.Comic));

        // Hard delete: the rows go with the node (FK cascade).
        await _t.Db.CatalogNodes.Where(n => n.Id == gone.Id).ExecuteDeleteAsync();
        Assert.False(await _t.Db.DeclaredFacts.AnyAsync(f => f.NodeId == gone.Id));

        // Tombstone: the row stays (the folder may come back) but neither it nor its subtree is listed.
        await _t.Db.CatalogNodes.Where(n => n.Id == ghost.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Availability, (int)CatalogNodeAvailability.Tombstoned));
        Assert.True(await _t.Db.DeclaredFacts.AnyAsync(f => f.NodeId == ghost.Id));

        var facts = await Reader().EffectiveForLibraryAsync(_t.LibraryId, CancellationToken.None);
        Assert.Equal(new[] { a.Id, b.Id }.Order(), facts.Keys.Order());
        Assert.False(facts.ContainsKey(below.Id));
    }

    [Fact]
    public async Task LibraryDelete_CascadesItsFacts()
    {
        var other = await _t.AddLibraryAsync("dellib", "Deleted");
        var folder = await _t.AddFolderAsync(null, "Shelf", other.Id);
        await SetLibraryAsync(Declare(DeclaredType.Manga), "dellib");
        await SetFolderAsync(folder, Declare(DeclaredType.Manhwa));
        Assert.Equal(2, await _t.Db.DeclaredFacts.CountAsync(f => f.LibraryId == other.Id));

        await _t.Db.Libraries.Where(l => l.Id == other.Id).ExecuteDeleteAsync();

        Assert.Equal(0, await _t.Db.DeclaredFacts.CountAsync(f => f.LibraryId == other.Id));
    }

    [Fact]
    public async Task Reader_UsesTwoQueriesPerLibrary_NotOnePerNode()
    {
        CatalogNodeEntity? parent = null;
        for (var i = 0; i < 30; i++)
            parent = await _t.AddFolderAsync(i % 5 == 0 ? null : parent, "Folder " + i);
        await SetLibraryAsync(Declare(DeclaredType.Manga));

        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(_t.Db.Database.GetConnectionString())
            .AddInterceptors(counter)
            .Options;
        await using var db = new MangaPixerDbContext(options);

        var facts = await new DeclaredFactsReader(db).EffectiveForLibraryAsync(_t.LibraryId, CancellationToken.None);

        Assert.Equal(30, facts.Count);
        Assert.Equal(2, counter.Count);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    // --- Admin edits ---

    [Fact]
    public async Task SetFolder_ValidatesAndCleans_KeepsOrder_Dedupes()
    {
        var folder = await _t.AddFolderAsync(null, "Series");
        var service = Service();

        Assert.Equal("creator_name_invalid", (await service.SetFolderAsync(folder.PublicId, Declare(null, ("  ", null)), "admin")).Error);
        Assert.Equal("creator_role_invalid", (await service.SetFolderAsync(folder.PublicId, Declare(null, ("Name", "colorist")), "admin")).Error);
        Assert.Equal("type_invalid", (await service.SetFolderAsync(folder.PublicId, new SetDeclaredFactsRequest { Type = (DeclaredType)99 }, "admin")).Error);
        var tooMany = Enumerable.Range(0, DeclaredFactKeys.MaxCreators + 1).Select(i => ("Name " + i, (string?)null)).ToArray();
        Assert.Equal("creators_too_many", (await service.SetFolderAsync(folder.PublicId, Declare(null, tooMany), "admin")).Error);
        Assert.False(await _t.Db.DeclaredFacts.AnyAsync());

        var result = await service.SetFolderAsync(folder.PublicId,
            Declare(DeclaredType.GraphicNovel, (" Second  Name ", "ARTIST"), ("First Name", null), ("second name", "artist")), "admin");

        Assert.Equal(MetadataLinkResultCode.Ok, result.Code);
        var own = result.Value!.Own;
        Assert.Equal(DeclaredType.GraphicNovel, own.Type);
        Assert.Equal(["Second Name", "First Name"], own.Creators.Select(c => c.Name));
        Assert.Equal(["artist", null], own.Creators.Select(c => c.Role));
        Assert.Equal("Series", result.Value.DisplayName);
        Assert.Equal(_t.LibraryPublicId, result.Value.LibraryId);
        Assert.Equal("graphic-novel", await _t.Db.DeclaredFacts.Where(f => f.Key == "type").Select(f => f.Value).SingleAsync());
        Assert.Equal(1, await _t.Db.AuditEvents.CountAsync(e => e.Action == AuditActions.DeclaredFactsSet));
    }

    [Fact]
    public async Task SetFolder_Replaces_ClearKeepsOtherKeys_AndUnchangedIsNotAudited()
    {
        var folder = await _t.AddFolderAsync(null, "Series");
        var service = Service();
        await SetFolderAsync(folder, Declare(DeclaredType.Manga, ("One", null), ("Two", null)));
        var createdOne = await _t.Db.DeclaredFacts.AsNoTracking().Where(f => f.Value == "One").Select(f => f.CreatedAt).SingleAsync();

        // A later key (e.g. genre) shares the table; v1 edits never touch it.
        _t.Db.DeclaredFacts.Add(new DeclaredFactEntity
        {
            LibraryId = _t.LibraryId,
            NodeId = folder.Id,
            Key = "genre",
            Value = "Synthetic Genre",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _t.Db.SaveChangesAsync();

        // Same values again: nothing written, nothing audited.
        await SetFolderAsync(folder, Declare(DeclaredType.Manga, ("One", null), ("Two", null)));
        Assert.Equal(1, await _t.Db.AuditEvents.CountAsync(e => e.Action == AuditActions.DeclaredFactsSet));

        // Reorder + replace: "One" keeps its row (CreatedAt), "Two" goes, "Three" comes.
        var replaced = await service.SetFolderAsync(folder.PublicId, Declare(DeclaredType.Manhwa, ("Three", null), ("One", null)), "admin");
        Assert.Equal(["Three", "One"], replaced.Value!.Own.Creators.Select(c => c.Name));
        _t.Db.ChangeTracker.Clear();
        Assert.Equal(createdOne, await _t.Db.DeclaredFacts.Where(f => f.Value == "One").Select(f => f.CreatedAt).SingleAsync());
        Assert.False(await _t.Db.DeclaredFacts.AnyAsync(f => f.Value == "Two"));

        var cleared = await service.ClearFolderAsync(folder.PublicId, "admin");
        Assert.Null(cleared.Value!.Own.Type);
        Assert.Empty(cleared.Value.Own.Creators);
        Assert.Equal("genre", (await _t.Db.DeclaredFacts.SingleAsync()).Key);
        Assert.Equal(1, await _t.Db.AuditEvents.CountAsync(e => e.Action == AuditActions.DeclaredFactsClear));
    }

    [Fact]
    public async Task SetFolder_ArchiveTombstonedOrUnknown_AreRejected()
    {
        var folder = await _t.AddFolderAsync(null, "Series");
        var archive = await _t.AddArchiveAsync(folder, "Series v01");
        var service = Service();

        Assert.Equal(MetadataLinkResultCode.NotAFolder, (await service.SetFolderAsync(archive.PublicId, Declare(DeclaredType.Manga), "admin")).Code);
        Assert.Equal(MetadataLinkResultCode.NodeNotFound, (await service.GetFolderAsync("nope")).Code);
        Assert.Equal(MetadataLinkResultCode.LibraryNotFound, (await service.GetLibraryAsync("nope")).Code);

        await _t.Db.CatalogNodes.Where(n => n.Id == folder.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Availability, (int)CatalogNodeAvailability.Tombstoned));
        Assert.Equal(MetadataLinkResultCode.NodeNotFound, (await service.SetFolderAsync(folder.PublicId, Declare(DeclaredType.Manga), "admin")).Code);
    }

    [Fact]
    public async Task GetFolder_ShowsOwnAndWhatIsInheritedFromAbove()
    {
        var shelf = await _t.AddFolderAsync(null, "Shelf");
        var series = await _t.AddFolderAsync(shelf, "Series");
        await SetLibraryAsync(Declare(null, ("Library Author", "author")));
        await SetFolderAsync(shelf, Declare(DeclaredType.Manhua));
        await SetFolderAsync(series, Declare(DeclaredType.Webtoon));

        var dto = (await Service().GetFolderAsync(series.PublicId)).Value!;

        Assert.Equal(DeclaredType.Webtoon, dto.Own.Type);
        Assert.Equal(DeclaredType.Manhua, dto.Inherited.Type);
        Assert.Equal(DeclaredFactSource.Inherited, dto.Inherited.TypeSource);
        Assert.Equal("Shelf", dto.Inherited.TypeFrom);
        Assert.Equal("Library Author", Assert.Single(dto.Inherited.Creators).Name);
        Assert.Equal(DeclaredFactSource.Library, dto.Inherited.CreatorsSource);
        Assert.Equal("Meta Lib", dto.Inherited.CreatorsFrom);

        var library = (await Service().GetLibraryAsync(_t.LibraryPublicId)).Value!;
        Assert.Null(library.NodeId);
        Assert.Equal("Meta Lib", library.DisplayName);
        Assert.Equal("Library Author", Assert.Single(library.Own.Creators).Name);
        Assert.Null(library.Inherited.Type);
    }

    // --- Info panel view ---

    [Fact]
    public async Task ForNode_ArchiveInheritsFromItsFolders()
    {
        var series = await _t.AddFolderAsync(null, "Series");
        var archive = await _t.AddArchiveAsync(series, "Series v01");
        await SetFolderAsync(series, Declare(DeclaredType.Manga, ("Series Author", "author")));

        var dto = await Service().ForNodeAsync(archive);

        Assert.Equal(archive.PublicId, dto.NodeId);
        Assert.Equal(DeclaredType.Manga, dto.Effective.Type);
        Assert.Equal(DeclaredFactSource.Inherited, dto.Effective.TypeSource);
        Assert.Equal("Series", dto.Effective.TypeFrom);
        Assert.Equal("Series Author", Assert.Single(dto.Effective.Creators).Name);
        Assert.Null(dto.Conflict);
    }

    [Fact]
    public async Task ForNode_ConflictWithTheLinkedRecord_ShowsBothSides()
    {
        var series = await _t.AddFolderAsync(null, "Series");
        var archive = await _t.AddArchiveAsync(series, "Series v01");
        var record = await _t.AddRecordAsync("9001", "Synthetic Web Title"); // Japan, comic, "Web Author"
        await _t.AddLinkAsync(series, record);

        // Agrees: manga + the same author in another word order -> no conflict.
        await SetFolderAsync(series, Declare(DeclaredType.Manga, ("AUTHOR, Web", null)));
        Assert.Null((await Service().ForNodeAsync(series)).Conflict);

        // Disagrees on both.
        await SetFolderAsync(series, Declare(DeclaredType.Manhwa, ("Someone Else", "artist")));
        var conflict = (await Service().ForNodeAsync(archive)).Conflict!;
        Assert.True(conflict.Type);
        Assert.Equal("Japan", conflict.RecordType); // no provider type stored: the origin name
        Assert.True(conflict.Creators);
        Assert.Equal(["Web Author"], conflict.RecordCreators);
        Assert.Equal("mangaupdates", conflict.ProviderName); // empty registry: the provider id

        // The provider's own type wins when stored.
        record.ProviderType = "Manga";
        await _t.Db.SaveChangesAsync();
        Assert.Equal("Manga", (await Service().ForNodeAsync(series)).Conflict!.RecordType);

        // Don't match below the link: no record applies, so no conflict.
        await _t.AddLinkAsync(archive, null, SeriesLinkState.DontMatch);
        Assert.Null((await Service().ForNodeAsync(archive)).Conflict);
    }

    [Fact]
    public async Task ForNode_ADeclaredOtherNameOfTheRecordsAuthor_IsNoConflict()
    {
        var series = await _t.AddFolderAsync(null, "Series");
        var record = await _t.AddRecordAsync("9002", "Synthetic Pen Title");
        record.CreatorsJson = MetadataJson.WriteList([new MetadataJson.Creator("Main Pen", "author", "4242")]);
        await _t.Db.SaveChangesAsync();
        await _t.AddLinkAsync(series, record);
        await SetFolderAsync(series, Declare(null, ("Second Pen", "author")));

        // No stored author record yet: the names differ.
        Assert.True((await Service().ForNodeAsync(series)).Conflict!.Creators);

        // Once "Artists' other names" stored the author, the declared pen name is one of them.
        _t.Db.MetadataAuthors.Add(new MetadataAuthorEntity
        {
            Provider = "mangaupdates",
            ExternalId = "4242",
            Name = "Main Pen",
            OtherNamesJson = """["Second Pen"]""",
            FetchedAt = DateTimeOffset.UnixEpoch,
            Status = 0,
        });
        await _t.Db.SaveChangesAsync();
        Assert.Null((await Service().ForNodeAsync(series)).Conflict);
    }

    [Fact]
    public async Task ForNode_SeriesInfoHidden_ShowsNothing()
    {
        var series = await _t.AddFolderAsync(null, "Series");
        await SetFolderAsync(series, Declare(DeclaredType.Manga));
        await _t.Db.Libraries.Where(l => l.Id == _t.LibraryId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.MetadataSeriesInfoHidden, true));

        var dto = await Service().ForNodeAsync(series);

        Assert.Null(dto.Effective.Type);
        Assert.Empty(dto.Effective.Creators);
        Assert.Null(dto.Conflict);
    }
}
