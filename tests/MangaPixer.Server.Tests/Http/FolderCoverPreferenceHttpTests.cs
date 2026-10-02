namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The per-folder cover preference (1.32.0) through the public surface: <c>GET / PUT / DELETE /admin/folders/{id}/cover-preference</c>
/// is admin only, validated (folders only, a known value) and audited with ids; and the preference reaches the browse cards - a
/// folder's "File covers" shows each file's own cover on every card below it (nested folders included), a nearer "Web covers when
/// available" brings the saved web cover back, and clearing a value inherits again. Synthetic rows and files only; no network.
/// </summary>
public sealed class FolderCoverPreferenceHttpTests
{
    private const string LibPub = "fcplib1";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record Seed(long LibraryId, CatalogNodeEntity Shelf, CatalogNodeEntity Series, CatalogNodeEntity Volume1, CatalogNodeEntity Volume2);

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, int kind, string name) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = kind,
        DisplayName = name,
        RelativePath = pub,
        PathKey = pub,
        SortKey = (kind == 0 ? "0" : "1") + name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Library > Shelf > Series > two volumes; volume 2 shows a stored web cover through an automatic decision.</summary>
    private static async Task<Seed> SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var thumbnails = scope.ServiceProvider.GetRequiredService<ThumbnailStore>();
        var files = scope.ServiceProvider.GetRequiredService<CoverFiles>();

        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Pref Lib", RootPath = "/synthetic/pref", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var shelf = Node("fcShelf", lib.Id, null, 0, "Shelf");
        db.CatalogNodes.Add(shelf);
        await db.SaveChangesAsync();
        var series = Node("fcSeries", lib.Id, shelf.Id, 0, "Series");
        db.CatalogNodes.Add(series);
        await db.SaveChangesAsync();
        var v1 = Node("fcVol1", lib.Id, series.Id, 1, "Series v01");
        var v2 = Node("fcVol2", lib.Id, series.Id, 1, "Series v02");
        db.CatalogNodes.AddRange(v1, v2);
        await db.SaveChangesAsync();
        foreach (var node in new[] { v1, v2 })
        {
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = node.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 1, ThumbnailState = 1, ThumbnailContentVersion = 1 });
            db.PageEntries.Add(new PageEntryEntity { ItemId = node.Id, ContentVersion = 1, Ordinal = 0, EntryKey = "p0", SourceEntryLocator = "p1.png", MediaType = "image/png", Width = 700, Height = 1000 });
            var temp = Path.Combine(factory.DataRoot, Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(temp, "file-" + node.PublicId);
            await thumbnails.PublishAsync(node.Id, 1, temp);
            File.Delete(temp);
        }

        var record = new MetadataRecordEntity { PublicId = "rfc1", Provider = "mangaupdates", ExternalId = "951", Title = "Series", FetchedAt = DateTimeOffset.UtcNow };
        var companion = new MetadataRecordEntity { PublicId = "mdfc1", Provider = "mangadex", ExternalId = "md-951", Title = "Series", FetchedAt = DateTimeOffset.UtcNow };
        db.MetadataRecords.AddRange(record, companion);
        await db.SaveChangesAsync();
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity { NodeId = series.Id, LibraryId = lib.Id, State = 0, RecordId = record.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.MetadataCompanions.Add(new MetadataCompanionEntity { RecordId = record.Id, Provider = "mangadex", CompanionRecordId = companion.Id, State = (int)CompanionState.Auto });
        var cover = new VolumeCoverEntity
        {
            PublicId = "vcfc0001",
            ProviderRecordId = companion.Id,
            Kind = (int)VolumeCoverKind.Volume,
            Volume = 2,
            Locale = "en",
            RemoteId = "r1",
            RemoteFile = "f1.jpg",
            State = (int)VolumeCoverState.Stored,
            StoredVersion = 1,
            Hash = 5,
            ListedAt = DateTimeOffset.UtcNow,
        };
        db.VolumeCovers.Add(cover);
        await db.SaveChangesAsync();
        db.NodeAutoCovers.Add(new NodeAutoCoverEntity
        {
            NodeId = v2.Id,
            Source = (int)AutoCoverSource.WebVolume,
            VolumeCoverId = cover.Id,
            Reason = (int)AutoCoverReason.LocalNotCover,
            InputsKey = "seeded",
            Version = 1,
            DecidedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var path = files.VolumeCoverPath(cover.PublicId, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "web-volume-2");
        return new Seed(lib.Id, shelf, series, v1, v2);
    }

    private static string Url(string nodeId) => $"/api/v1/admin/folders/{nodeId}/cover-preference";

    private static async Task<FolderCoverPreferenceDto> OkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FolderCoverPreferenceDto>(JsonOptions))!;
    }

    private static async Task<CatalogNodeDto> CardOfV2Async(HttpClient client, Seed seed) =>
        (await client.GetFromJsonAsync<PageResponse<CatalogNodeDto>>(
            $"/api/v1/libraries/{LibPub}/browse?sort=name&parentId={seed.Series.PublicId}", JsonOptions))!.Items.Single(i => i.Id == seed.Volume2.PublicId);

    /// <summary>Writes (or removes) a folder's row straight into the database: no request, so no background decision races the read.</summary>
    private static async Task SetRowAsync(MetadataNetworkWebApplicationFactory factory, CatalogNodeEntity folder, FolderCoverPreference? preference)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        await db.FolderCoverPreferences.Where(f => f.NodeId == folder.Id).ExecuteDeleteAsync();
        if (preference is { } value)
        {
            db.FolderCoverPreferences.Add(new FolderCoverPreferenceEntity { NodeId = folder.Id, Preference = (int)value });
            await db.SaveChangesAsync();
        }
    }

    private static async Task<int?> DecisionSourceAsync(MetadataNetworkWebApplicationFactory factory, Seed seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await db.NodeAutoCovers.AsNoTracking().Where(a => a.NodeId == seed.Volume2.Id).Select(a => (int?)a.Source).FirstOrDefaultAsync();
    }

    /// <summary>Waits for the hosted service to drain the "decide this subtree soon" request a change queued.</summary>
    private static async Task WaitForDecisionDroppedAsync(MetadataNetworkWebApplicationFactory factory, Seed seed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await DecisionSourceAsync(factory, seed) is not null && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Null(await DecisionSourceAsync(factory, seed));
    }

    /// <summary>Puts volume 2's web decision back (the real pass cannot hash this suite's synthetic thumbnails; the service tests cover it).</summary>
    private static async Task ReseedDecisionAsync(MetadataNetworkWebApplicationFactory factory, Seed seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var cover = await db.VolumeCovers.AsNoTracking().SingleAsync();
        db.NodeAutoCovers.Add(new NodeAutoCoverEntity
        {
            NodeId = seed.Volume2.Id,
            Source = (int)AutoCoverSource.WebVolume,
            VolumeCoverId = cover.Id,
            Reason = (int)AutoCoverReason.LocalNotCover,
            InputsKey = "reseeded",
            Version = 2,
            DecidedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Endpoints_AreAdminOnly_ValidateAndAuditWithIds()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var seed = await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var member = await factory.CreateReaderClientAsync("member", grantLibrary: LibPub);

        // Not an admin: forbidden on all three verbs.
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(Url(seed.Shelf.PublicId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync(Url(seed.Shelf.PublicId), new SetFolderCoverPreferenceRequest { Preference = FolderCoverPreference.File }, JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync(Url(seed.Shelf.PublicId))).StatusCode);

        var initial = await OkAsync(await admin.GetAsync(Url(seed.Series.PublicId)));
        Assert.Equal((null, FolderCoverPreference.Web, FolderCoverPreference.Web, (string?)null), (initial.Preference, initial.Effective, initial.Inherited, initial.InheritedSourceName));

        var set = await OkAsync(await admin.PutAsJsonAsync(Url(seed.Shelf.PublicId), new SetFolderCoverPreferenceRequest { Preference = FolderCoverPreference.File }, JsonOptions));
        Assert.Equal((FolderCoverPreference.File, FolderCoverPreference.File), (set.Preference, set.Effective));
        var inherited = await OkAsync(await admin.GetAsync(Url(seed.Series.PublicId)));
        Assert.Equal((null, FolderCoverPreference.File, FolderCoverPreference.File, seed.Shelf.PublicId, "Shelf"),
            (inherited.Preference, inherited.Effective, inherited.Inherited, inherited.InheritedSourceNodeId, inherited.InheritedSourceName));

        var cleared = await OkAsync(await admin.DeleteAsync(Url(seed.Shelf.PublicId)));
        Assert.Equal((null, FolderCoverPreference.Web), (cleared.Preference, cleared.Effective));

        // Validation: an archive is not a folder, an unknown node is a 404, a value outside the enum is a 400.
        var archive = await admin.PutAsJsonAsync(Url(seed.Volume1.PublicId), new SetFolderCoverPreferenceRequest { Preference = FolderCoverPreference.File }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, archive.StatusCode);
        Assert.Equal("not_a_folder", (await archive.Content.ReadFromJsonAsync<ApiError>(JsonOptions))!.Error);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(Url("nonexistent"))).StatusCode);
        var invalid = await admin.PutAsync(Url(seed.Shelf.PublicId), JsonContent.Create(new { preference = "Sideways" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var outOfRange = await admin.PutAsync(Url(seed.Shelf.PublicId), JsonContent.Create(new { preference = 7 }));
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);

        // Audited: who (an admin) did what on which folder - ids and the value, never a name.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var audit = await db.AuditEvents.AsNoTracking().Where(e => e.Action.StartsWith("folder.cover-preference")).OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(["folder.cover-preference.set", "folder.cover-preference.clear"], audit.Select(e => e.Action));
        Assert.All(audit, e => Assert.Equal((seed.LibraryId, seed.Shelf.Id), (e.TargetLibraryId!.Value, e.TargetItemId!.Value)));
        Assert.NotNull(audit[0].ActorUserId);
        Assert.Equal("File", audit[0].Result);
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task ThePreference_ReachesTheBrowseCards_InheritedByNestedFolders_AndClearingInheritsAgain()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var seed = await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var member = await factory.CreateReaderClientAsync("member", grantLibrary: LibPub);

        var card = await CardOfV2Async(member, seed);
        Assert.Equal(CardCoverSource.WebVolume, card.CoverSource);
        Assert.Equal("web-volume-2", await member.GetStringAsync(card.CoverUrl));

        // File covers on the SHELF (through the endpoint): the card two levels down shows the file's own cover.
        await OkAsync(await admin.PutAsJsonAsync(Url(seed.Shelf.PublicId), new SetFolderCoverPreferenceRequest { Preference = FolderCoverPreference.File }, JsonOptions));
        card = await CardOfV2Async(member, seed);
        Assert.Equal(CardCoverSource.File, card.CoverSource);
        Assert.Equal("file-" + seed.Volume2.PublicId, await member.GetStringAsync(card.CoverUrl));

        // The background pass dropped the web decision (it offers no web source under File covers). Put it back by hand and change
        // the rows directly from here on: the rest of the test reads what the cards say, with no background pass in between.
        await WaitForDecisionDroppedAsync(factory, seed);
        await ReseedDecisionAsync(factory, seed);
        Assert.Equal(CardCoverSource.File, (await CardOfV2Async(member, seed)).CoverSource); // still hidden: the row says so, not the decision

        // Web covers on the series folder (nearer): the saved cover is back.
        await SetRowAsync(factory, seed.Series, FolderCoverPreference.Web);
        Assert.Equal(CardCoverSource.WebVolume, (await CardOfV2Async(member, seed)).CoverSource);

        // The library hiding web covers does not override a folder that says Web covers; clearing it inherits File covers again.
        var off = await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}", new UpdateMetadataLibraryRequest { ShowWebCovers = false }, JsonOptions);
        Assert.True(off.IsSuccessStatusCode);
        Assert.Equal(CardCoverSource.WebVolume, (await CardOfV2Async(member, seed)).CoverSource);
        await SetRowAsync(factory, seed.Series, null);
        Assert.Equal(CardCoverSource.File, (await CardOfV2Async(member, seed)).CoverSource);

        // Everything cleared and the library showing web covers again: the original card.
        await SetRowAsync(factory, seed.Shelf, null);
        Assert.Equal(CardCoverSource.File, (await CardOfV2Async(member, seed)).CoverSource); // the library still hides them
        await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}", new UpdateMetadataLibraryRequest { ShowWebCovers = true }, JsonOptions);
        Assert.Equal(CardCoverSource.WebVolume, (await CardOfV2Async(member, seed)).CoverSource);
    }

    [Fact]
    public async Task ChangingThePreference_MakesTheCoverLayerDecideTheSubtreeAgain_InTheBackground()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var seed = await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        Assert.Equal((int)AutoCoverSource.WebVolume, await DecisionSourceAsync(factory, seed));

        // The hosted service drains the "decide this subtree" request: under File covers the web decision of volume 2 is dropped.
        await OkAsync(await admin.PutAsJsonAsync(Url(seed.Shelf.PublicId), new SetFolderCoverPreferenceRequest { Preference = FolderCoverPreference.File }, JsonOptions));
        await WaitForDecisionDroppedAsync(factory, seed);
    }
}
