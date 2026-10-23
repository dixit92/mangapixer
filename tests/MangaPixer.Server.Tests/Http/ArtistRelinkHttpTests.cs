namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests for the local relink of a declared artist (1.45.0, owner 2026-10-19 - lane A question 3, "Yes and attempt to relink"):
/// renaming the declared artist in Declared facts drops its MangaUpdates reference (as before) and then gives the new name the id of the
/// one provider-backed creator known here by that name - a stored MangaUpdates author record (main or other name) or a tag-index creator
/// with a MangaUpdates id; none when two ids share the name. Marking from the Artist folder dialog without a picked author relinks the
/// same way; Match folders by name does not (it decides its own ids). Audited as the artist-folder / declared-facts rows are. Every host
/// fails on any outgoing request: nothing is sent. Synthetic names; raw JSON / base types only.
/// </summary>
[Trait("Category", "Http")]
public sealed class ArtistRelinkHttpTests
{
    private const string LibPub = "rllib1";

    // Stored MangaUpdates author records: 111 "Gamma Inker Main" (other name "Gamma Inker"), 222 "Shared Pen".
    // Tag index: a creator "Delta Writer" with MangaUpdates id 444; a creator "Shared Pen" with id 333 (another person by that name).
    private static async Task SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var now = DateTimeOffset.UtcNow;
        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Relink Lib", RootPath = "/synthetic/rl", CreatedAt = now };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        foreach (var (pub, name) in new[] { ("rlRename", "Beta Painter"), ("rlGamma", "Gamma Inker"), ("rlShared", "Shared Pen"), ("rlNobody", "Nobody Here"), ("rlBulk", "Gamma Inker"), ("rlPlain", "Plain Folder"), ("rlCaps", "Caps Folder") })
        {
            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = pub,
                LibraryId = lib.Id,
                Kind = (int)CatalogNodeKind.Folder,
                DisplayName = name,
                RelativePath = pub,
                PathKey = pub,
                SortKey = "0" + name,
                CreatedAt = now,
            });
        }
        db.MetadataAuthors.AddRange(
            new MetadataAuthorEntity { Provider = "mangaupdates", ExternalId = "111", Name = "Gamma Inker Main", OtherNamesJson = MetadataJson.WriteList(new[] { "Gamma Inker" }), FetchedAt = now },
            new MetadataAuthorEntity { Provider = "mangaupdates", ExternalId = "222", Name = "Shared Pen", FetchedAt = now });
        var delta = new CreatorEntity { PublicId = "cr_rltest1", DisplayName = "Delta Writer", CreatedAt = now, UpdatedAt = now };
        var shared = new CreatorEntity { PublicId = "cr_rltest2", DisplayName = "Shared Pen", CreatedAt = now, UpdatedAt = now };
        db.Creators.AddRange(delta, shared);
        await db.SaveChangesAsync();
        db.CreatorNames.AddRange(
            new CreatorNameEntity { CreatorId = delta.Id, Name = "Delta Writer", NameKey = DeclaredFactsComparer.NameKey("Delta Writer") },
            new CreatorNameEntity { CreatorId = shared.Id, Name = "Shared Pen", NameKey = DeclaredFactsComparer.NameKey("Shared Pen") });
        db.CreatorProviderIds.AddRange(
            new CreatorProviderIdEntity { CreatorId = delta.Id, Provider = "mangaupdates", ExternalId = "444" },
            new CreatorProviderIdEntity { CreatorId = shared.Id, Provider = "mangaupdates", ExternalId = "333" });
        await db.SaveChangesAsync();
    }

    private static async Task<(string Name, string? Ref)> ArtistAsync(MetadataNetworkWebApplicationFactory factory, string nodePublicId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var nodeId = await db.CatalogNodes.Where(n => n.PublicId == nodePublicId).Select(n => n.Id).SingleAsync();
        var row = await db.DeclaredFacts.AsNoTracking().SingleAsync(f => f.NodeId == nodeId && f.Key == DeclaredFactKeys.Creator);
        return (row.Value, row.ExternalRef);
    }

    private static async Task<List<string>> AuditResultsAsync(MetadataNetworkWebApplicationFactory factory, string action, string nodePublicId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var nodeId = await db.CatalogNodes.Where(n => n.PublicId == nodePublicId).Select(n => n.Id).SingleAsync();
        return await db.AuditEvents.AsNoTracking().Where(a => a.Action == action && a.TargetItemId == nodeId).OrderBy(a => a.Id)
            .Select(a => a.Result).ToListAsync();
    }

    private static async Task RenameAsync(HttpClient admin, string nodePublicId, string name) =>
        (await admin.PutAsJsonAsync($"/api/v1/admin/metadata/folders/{nodePublicId}/declared",
            new SetDeclaredFactsRequest { Creators = [new DeclaredCreatorDto { Name = name, Role = "author" }] })).EnsureSuccessStatusCode();

    [Fact]
    public async Task RenamingTheDeclaredArtist_DropsTheReference_ThenRelinksToTheOneCreatorKnownByTheNewName_SendingNothing()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/rlRename/artist-folder",
            new SetArtistFolderRequest { Name = "Beta Painter", Role = "author", Provider = "mangaupdates", ExternalId = "4815162342" })).EnsureSuccessStatusCode();
        Assert.Equal(("Beta Painter", "mangaupdates:4815162342"), await ArtistAsync(factory, "rlRename"));

        // A tag-index creator with a MangaUpdates id.
        await RenameAsync(admin, "rlRename", "Delta Writer");
        Assert.Equal(("Delta Writer", "mangaupdates:444"), await ArtistAsync(factory, "rlRename"));
        // A stored author record's other name (any name order / case).
        await RenameAsync(admin, "rlRename", "inker gamma");
        Assert.Equal(("inker gamma", "mangaupdates:111"), await ArtistAsync(factory, "rlRename"));
        // Two people by that name (a stored author record and another tag-index creator): the reference stays empty.
        await RenameAsync(admin, "rlRename", "Shared Pen");
        Assert.Equal(("Shared Pen", (string?)null), await ArtistAsync(factory, "rlRename"));

        // The first row is the mark itself (it declared the artist); then the three renames.
        Assert.Equal(["success", "local_relink", "local_relink", "success"], await AuditResultsAsync(factory, "metadata.declared.set", "rlRename"));
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task TheArtistFolderDialog_WithoutAPick_RelinksLocally_ButMatchFoldersByNameDoesNot()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var marked = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/rlGamma/artist-folder", new SetArtistFolderRequest { Role = "author" });
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        using (var doc = JsonDocument.Parse(await marked.Content.ReadAsStringAsync()))
            Assert.Equal("mangaupdates:111", doc.RootElement.GetProperty("authorRef").GetString());
        Assert.Equal(("Gamma Inker", "mangaupdates:111"), await ArtistAsync(factory, "rlGamma"));
        Assert.Equal(["local_relink"], await AuditResultsAsync(factory, "metadata.artist_folder", "rlGamma"));

        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/rlShared/artist-folder", new SetArtistFolderRequest())).EnsureSuccessStatusCode();
        Assert.Equal(("Shared Pen", (string?)null), await ArtistAsync(factory, "rlShared"));
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/rlNobody/artist-folder", new SetArtistFolderRequest())).EnsureSuccessStatusCode();
        Assert.Equal(("Nobody Here", (string?)null), await ArtistAsync(factory, "rlNobody"));
        Assert.Equal(["success"], await AuditResultsAsync(factory, "metadata.artist_folder", "rlNobody"));

        // Match folders by name passes the id it found itself (or none): no relink there.
        (await admin.PostAsJsonAsync("/api/v1/admin/metadata/folder-match/apply", new FolderMatchApplyRequest
        {
            Kind = FolderMatchKind.Artists,
            Items = [new FolderMatchApplyItem { NodeId = "rlBulk", Name = "Gamma Inker", Role = "author" }],
        })).EnsureSuccessStatusCode();
        Assert.Equal(("Gamma Inker", (string?)null), await ArtistAsync(factory, "rlBulk"));
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task APlainNewDeclaredCreator_TriesTheLocalRelink_WithoutARemovedRow_AndAnUnknownNameStaysEmpty()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        // A folder with no declared creator yet: adding "Delta Writer" (a tag-index creator with a MangaUpdates id) links it.
        await RenameAsync(admin, "rlPlain", "Delta Writer");
        Assert.Equal(("Delta Writer", "mangaupdates:444"), await ArtistAsync(factory, "rlPlain"));
        Assert.Equal(["local_relink"], await AuditResultsAsync(factory, "metadata.declared.set", "rlPlain"));

        // Not known here, or known under two ids: no reference, plain success.
        await RenameAsync(admin, "rlCaps", "Nobody Known");
        Assert.Equal(("Nobody Known", (string?)null), await ArtistAsync(factory, "rlCaps"));
        Assert.Equal(["success"], await AuditResultsAsync(factory, "metadata.declared.set", "rlCaps"));
        await RenameAsync(admin, "rlCaps", "Shared Pen");
        Assert.Equal(("Shared Pen", (string?)null), await ArtistAsync(factory, "rlCaps"));
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task ACapitalsOnlyRename_KeepsTheReference_ButARealRenameStillDropsIt()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/rlCaps/artist-folder",
            new SetArtistFolderRequest { Name = "Beta Painter", Role = "author", Provider = "mangaupdates", ExternalId = "4815162342" })).EnsureSuccessStatusCode();
        Assert.Equal(("Beta Painter", "mangaupdates:4815162342"), await ArtistAsync(factory, "rlCaps"));

        // Only capitals (and the word order, which the name key ignores) change: the reference is carried over, nothing is looked up again.
        await RenameAsync(admin, "rlCaps", "BETA painter");
        Assert.Equal(("BETA painter", "mangaupdates:4815162342"), await ArtistAsync(factory, "rlCaps"));
        await RenameAsync(admin, "rlCaps", "Painter Beta");
        Assert.Equal(("Painter Beta", "mangaupdates:4815162342"), await ArtistAsync(factory, "rlCaps"));
        // Other punctuation / accents fold as well (the name key's own rule).
        await RenameAsync(admin, "rlCaps", "Béta-Painter");
        Assert.Equal(("Béta-Painter", "mangaupdates:4815162342"), await ArtistAsync(factory, "rlCaps"));

        // A different name drops it (as before); a local relink may then give the new name another one - here none is known.
        await RenameAsync(admin, "rlCaps", "Other Painter");
        Assert.Equal(("Other Painter", (string?)null), await ArtistAsync(factory, "rlCaps"));
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task MatchFoldersByName_AuditsALocalAuthorMatch_ApartFromAnAuthorSearchPick()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var applied = await admin.PostAsJsonAsync("/api/v1/admin/metadata/folder-match/apply", new FolderMatchApplyRequest
        {
            Kind = FolderMatchKind.Artists,
            Items =
            [
                new FolderMatchApplyItem { NodeId = "rlGamma", Name = "Gamma Inker", Role = "author", Provider = "mangaupdates", ExternalId = "111", LocalMatch = true },
                new FolderMatchApplyItem { NodeId = "rlShared", Name = "Shared Pen", Role = "author", Provider = "mangaupdates", ExternalId = "222" },
                new FolderMatchApplyItem { NodeId = "rlBulk", Name = "Gamma Inker", Role = "author" },
            ],
        });
        applied.EnsureSuccessStatusCode();
        Assert.Equal(("Gamma Inker", "mangaupdates:111"), await ArtistAsync(factory, "rlGamma"));
        Assert.Equal(["local_author_match"], await AuditResultsAsync(factory, "metadata.artist_folder", "rlGamma"));
        Assert.Equal(("Shared Pen", "mangaupdates:222"), await ArtistAsync(factory, "rlShared"));
        Assert.Equal(["author_search"], await AuditResultsAsync(factory, "metadata.artist_folder", "rlShared"));
        Assert.Equal(["folder_match"], await AuditResultsAsync(factory, "metadata.artist_folder", "rlBulk"));
        Assert.Equal(0, factory.Handler.CallCount);
    }
}
