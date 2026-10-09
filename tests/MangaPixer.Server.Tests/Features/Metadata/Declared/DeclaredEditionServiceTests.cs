namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Declared;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Declared;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests of the folder's own edition facts (1.39.0, owner 2026-10-09): "Volumes in this edition", the edition label and
/// "Track completion" - stored as their own keys, folder scope only (never inherited, never on a library), never matcher input, and
/// saved apart from the type / creators so neither save wipes the other. Synthetic names only.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class DeclaredEditionServiceTests : IAsyncLifetime
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
        TimeProvider.System);

    private static SetDeclaredEditionRequest Edition(int? volumes, DeclaredEdition? edition = null, bool tracking = true) =>
        new() { VolumeTotal = volumes, Edition = edition, Tracking = tracking };

    private async Task<List<(string Key, string Value)>> RowsAsync(long nodeId) =>
        (await _t.Db.DeclaredFacts.AsNoTracking().Where(f => f.NodeId == nodeId).OrderBy(f => f.Key).ThenBy(f => f.Position)
            .Select(f => new { f.Key, f.Value }).ToListAsync()).Select(r => (r.Key, r.Value)).ToList();

    [Fact]
    public async Task EditionSave_LeavesTypeAndCreators_AndATypeSaveLeavesTheEdition()
    {
        var folder = await _t.AddFolderAsync(null, "Synthetic Omnibus");
        var service = Service();
        await service.SetFolderAsync(folder.PublicId, new SetDeclaredFactsRequest
        {
            Type = DeclaredType.Manga,
            Creators = [new DeclaredCreatorDto { Name = "Synthetic Author" }],
        }, "admin");

        var saved = await service.SetFolderEditionAsync(folder.PublicId, Edition(12, DeclaredEdition.Omnibus, tracking: false), "admin");

        Assert.Equal(MetadataLinkResultCode.Ok, saved.Code);
        Assert.Equal(DeclaredType.Manga, saved.Value!.Own.Type);
        Assert.Equal("Synthetic Author", Assert.Single(saved.Value.Own.Creators).Name);
        Assert.Equal(new DeclaredEditionDto { VolumeTotal = 12, Edition = DeclaredEdition.Omnibus, Tracking = false }, saved.Value.Own.Edition);
        // The stored form: one row per key, slugs and invariant numbers.
        Assert.Equal(
            [("creator", "Synthetic Author"), ("edition", "omnibus"), ("tracking", "off"), ("type", "manga"), ("volumes", "12")],
            await RowsAsync(folder.Id));

        // A type / creator save (what every older client sends) leaves the edition facts alone.
        var retyped = await service.SetFolderAsync(folder.PublicId, new SetDeclaredFactsRequest { Type = DeclaredType.Manhwa }, "admin");
        Assert.Equal(DeclaredType.Manhwa, retyped.Value!.Own.Type);
        Assert.Empty(retyped.Value.Own.Creators);
        Assert.Equal(12, retyped.Value.Own.Edition!.VolumeTotal);

        // Tracking back on: no row for it; volumes and label cleared: no edition at all.
        var on = await service.SetFolderEditionAsync(folder.PublicId, Edition(12), "admin");
        Assert.Equal(new DeclaredEditionDto { VolumeTotal = 12, Edition = null, Tracking = true }, on.Value!.Own.Edition);
        Assert.Equal([("type", "manhwa"), ("volumes", "12")], await RowsAsync(folder.Id));
        var none = await service.SetFolderEditionAsync(folder.PublicId, Edition(null), "admin");
        Assert.Null(none.Value!.Own.Edition);
        Assert.Equal([("type", "manhwa")], await RowsAsync(folder.Id));
    }

    [Fact]
    public async Task EditionSave_Validates_FoldersOnly_AndUnchangedIsNotAudited()
    {
        var folder = await _t.AddFolderAsync(null, "Synthetic Master");
        var archive = await _t.AddArchiveAsync(folder, "Synthetic Master v01");
        var service = Service();

        Assert.Equal("volumes_invalid", (await service.SetFolderEditionAsync(folder.PublicId, Edition(0), "admin")).Error);
        Assert.Equal("volumes_invalid", (await service.SetFolderEditionAsync(folder.PublicId, Edition(DeclaredFactKeys.MaxVolumeTotal + 1), "admin")).Error);
        Assert.Equal("edition_invalid", (await service.SetFolderEditionAsync(folder.PublicId, Edition(3, (DeclaredEdition)9), "admin")).Error);
        Assert.Equal(MetadataLinkResultCode.NotAFolder, (await service.SetFolderEditionAsync(archive.PublicId, Edition(3), "admin")).Code);
        Assert.Equal(MetadataLinkResultCode.NodeNotFound, (await service.SetFolderEditionAsync("nope", Edition(3), "admin")).Code);
        Assert.False(await _t.Db.DeclaredFacts.AnyAsync());

        await service.SetFolderEditionAsync(folder.PublicId, Edition(DeclaredFactKeys.MaxVolumeTotal, DeclaredEdition.Master), "admin");
        await service.SetFolderEditionAsync(folder.PublicId, Edition(DeclaredFactKeys.MaxVolumeTotal, DeclaredEdition.Master), "admin");
        Assert.Equal(1, await _t.Db.AuditEvents.CountAsync(e => e.Action == AuditActions.DeclaredFactsSet));

        // "Clear" removes everything declared on the folder, the edition facts included.
        await service.SetFolderAsync(folder.PublicId, new SetDeclaredFactsRequest { Type = DeclaredType.Comic }, "admin");
        var cleared = await service.ClearFolderAsync(folder.PublicId, "admin");
        Assert.Null(cleared.Value!.Own.Type);
        Assert.Null(cleared.Value.Own.Edition);
        Assert.False(await _t.Db.DeclaredFacts.AnyAsync());
    }

    [Fact]
    public async Task EditionFacts_AreOwnScopeOnly_NeverInherited_NeverMatcherInput()
    {
        var series = await _t.AddFolderAsync(null, "Synthetic Series");
        var season = await _t.AddFolderAsync(series, "Season 2");
        var archive = await _t.AddArchiveAsync(series, "Synthetic Series v01");
        var service = Service();
        await service.SetFolderEditionAsync(series.PublicId, Edition(6, DeclaredEdition.Deluxe, tracking: false), "admin");

        // The folder itself: shown in its scope and on its Info panel line.
        Assert.Equal(6, (await service.GetFolderAsync(series.PublicId)).Value!.Own.Edition!.VolumeTotal);
        Assert.Equal(DeclaredEdition.Deluxe, (await service.ForNodeAsync(series)).Edition!.Edition);
        // Not the subfolder, not the archive inside it.
        Assert.Null((await service.GetFolderAsync(season.PublicId)).Value!.Own.Edition);
        Assert.Null((await service.ForNodeAsync(season)).Edition);
        Assert.Null((await service.ForNodeAsync(archive)).Edition);
        // A library scope has none.
        Assert.Null((await service.GetLibraryAsync(_t.LibraryPublicId)).Value!.Own.Edition);
        // Matching reads type / creators only: a folder with only edition facts declares nothing to the matcher.
        Assert.Empty(await new DeclaredFactsReader(_t.Db).EffectiveForLibraryAsync(_t.LibraryId, default));
    }
}
