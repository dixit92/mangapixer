namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Facets;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Authors;
using com.lifepixer.mangapixer.Server.Features.Metadata.Declared;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>Records every nudge (thread-safe: the scan and the backfill nudge from background tasks).</summary>
public sealed class RecordingFacetIndexNudge : IFacetIndexNudge
{
    private readonly ConcurrentQueue<(long? LibraryId, FacetNudgeReason Reason)> _nudges = new();

    /// <summary>(library id, reason) in order; a null library id is <see cref="AllChanged"/>.</summary>
    public IReadOnlyList<(long? LibraryId, FacetNudgeReason Reason)> Nudges => _nudges.ToList();

    public void LibraryChanged(long libraryId, FacetNudgeReason reason) => _nudges.Enqueue((libraryId, reason));

    public void AllChanged(FacetNudgeReason reason) => _nudges.Enqueue((null, reason));

    public void Clear() => _nudges.Clear();
}

/// <summary>
/// The 1.45.0 tag-index seam (I0): the writers of the index's inputs reach <see cref="IFacetIndexNudge"/> after their write, through
/// the app's public surface - admin link writes, declared facts, a finished library scan and a record stored / refreshed (HTTP, the
/// app's own DI with a recording nudge swapped in); the ComicInfo backfill batch (unit, the delegate-driven pass); and the wiring of
/// every call-site class to the registered nudge (DI + reflection - covers the author look-up, whose run needs a provider).
/// </summary>
public sealed class FacetIndexNudgeTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    private static (long? LibraryId, FacetNudgeReason Reason) N(long? libraryId, FacetNudgeReason reason) => (libraryId, reason);

    private static ApiTokenTestFactory Factory(RecordingFacetIndexNudge recorder) => new(configureServices: services =>
    {
        services.RemoveAll<IFacetIndexNudge>();
        services.AddSingleton<IFacetIndexNudge>(recorder);
    });

    private static CatalogNodeEntity Folder(long libraryId, string publicId, string name) => new()
    {
        PublicId = publicId,
        LibraryId = libraryId,
        Kind = (int)CatalogNodeKind.Folder,
        DisplayName = name,
        RelativePath = name,
        PathKey = name.ToLowerInvariant(),
        SortKey = "0" + name.ToLowerInvariant(),
        Availability = (int)CatalogNodeAvailability.Available,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>A library with one folder and one stored record (no network needed to link it); returns the library id.</summary>
    private static async Task<long> SeedAsync(IServiceProvider services, string libraryPublicId, string folderPublicId, string rootPath = "/synthetic/facets")
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var library = new LibraryEntity { PublicId = libraryPublicId, DisplayName = "Facets", RootPath = rootPath, CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        db.CatalogNodes.Add(Folder(library.Id, folderPublicId, "Series"));
        db.MetadataRecords.Add(new MetadataRecordEntity
        {
            PublicId = "fnrec1",
            Provider = "mangaupdates",
            ExternalId = "424242",
            Title = "Synthetic Web Title",
            FetchedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return library.Id;
    }

    [Fact]
    public async Task AdminLinkWrites_NudgeTheirLibrary_AfterTheWrite()
    {
        var recorder = new RecordingFacetIndexNudge();
        using var factory = Factory(recorder);
        var admin = await factory.AdminAsync();
        var libraryId = await SeedAsync(factory.Services, "fnlib1", "fnFolder");
        recorder.Clear();

        var link = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/fnFolder/link", new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = "424242" });
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        Assert.Equal(new[] { N(libraryId, FacetNudgeReason.LinkChanged) }, recorder.Nudges);
        using (var scope = factory.Services.CreateScope())
        {
            // The nudge came after the commit: the link it announces is there.
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            Assert.True(await db.NodeSeriesLinks.AnyAsync(l => l.LibraryId == libraryId && l.State == (int)SeriesLinkState.Confirmed));
        }

        recorder.Clear();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/fnFolder/dont-match", new { })).StatusCode);
        Assert.Equal(new[] { N(libraryId, FacetNudgeReason.LinkChanged) }, recorder.Nudges);

        recorder.Clear();
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/v1/admin/metadata/nodes/fnFolder/dont-match")).StatusCode);
        Assert.Equal(new[] { N(libraryId, FacetNudgeReason.LinkChanged) }, recorder.Nudges);

        // An unknown node writes nothing and nudges nothing.
        recorder.Clear();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/nope/dont-match", new { })).StatusCode);
        Assert.Empty(recorder.Nudges);
    }

    [Fact]
    public async Task DeclaredFacts_SetAndClear_NudgeTheirLibrary_AndAnUnchangedSaveDoesNot()
    {
        var recorder = new RecordingFacetIndexNudge();
        using var factory = Factory(recorder);
        var admin = await factory.AdminAsync();
        var libraryId = await SeedAsync(factory.Services, "fnlib2", "fnDeclared");
        recorder.Clear();

        var request = new SetDeclaredFactsRequest { Creators = [new DeclaredCreatorDto { Name = "Synthetic Artist", Role = "artist" }] };
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/fnDeclared/declared", request)).StatusCode);
        Assert.Equal(new[] { N(libraryId, FacetNudgeReason.DeclaredFactsChanged) }, recorder.Nudges);

        recorder.Clear();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/fnDeclared/declared", request)).StatusCode);
        Assert.Empty(recorder.Nudges); // nothing changed, nothing written

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/v1/admin/metadata/folders/fnDeclared/declared")).StatusCode);
        Assert.Equal(new[] { N(libraryId, FacetNudgeReason.DeclaredFactsChanged) }, recorder.Nudges);
    }

    [Fact]
    public async Task AFinishedLibraryScan_NudgesThatLibrary()
    {
        var recorder = new RecordingFacetIndexNudge();
        using var factory = Factory(recorder);
        var admin = await factory.AdminAsync();
        var root = Path.Combine(Path.GetTempPath(), "mangapixer-fnscan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "Series A"));
        _roots.Add(root);
        var libraryId = await SeedAsync(factory.Services, "fnlib3", "fnScanFolder", root);
        recorder.Clear();

        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/v1/admin/libraries/fnlib3/scan", null)).StatusCode);
        for (var i = 0; i < 300 && !recorder.Nudges.Contains(N(libraryId, FacetNudgeReason.ScanFinished)); i++)
            await Task.Delay(50);

        Assert.Contains(N(libraryId, FacetNudgeReason.ScanFinished), recorder.Nudges);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        Assert.Equal(2, (await db.ScanRuns.AsNoTracking().SingleAsync(r => r.LibraryId == libraryId)).Status); // completed before the nudge
    }

    [Fact]
    public async Task ARecordStoredOrRefreshedInIdentify_NudgesOnlyTheLibrariesThatLinkIt()
    {
        var recorder = new RecordingFacetIndexNudge();
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IFacetIndexNudge>();
            services.AddSingleton<IFacetIndexNudge>(recorder);
        });
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        long libraryId;
        long secondId;
        long thirdId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var library = new LibraryEntity { PublicId = "fnlib4", DisplayName = "Net", RootPath = "/synthetic/fnnet", CreatedAt = DateTimeOffset.UtcNow };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            db.CatalogNodes.Add(Folder(library.Id, "fnNetSeries", "Berserk"));
            await db.SaveChangesAsync();
            libraryId = library.Id;
            // 1.45.1 (B2): a second library linking the same series, and a third that does not link it.
            var second = new LibraryEntity { PublicId = "fnlib5", DisplayName = "Second", RootPath = "/synthetic/fnnet2", CreatedAt = DateTimeOffset.UtcNow };
            var third = new LibraryEntity { PublicId = "fnlib6", DisplayName = "Third", RootPath = "/synthetic/fnnet3", CreatedAt = DateTimeOffset.UtcNow };
            db.Libraries.AddRange(second, third);
            await db.SaveChangesAsync();
            db.CatalogNodes.Add(Folder(second.Id, "fnNetSecond", "Berserk"));
            db.CatalogNodes.Add(Folder(third.Id, "fnNetThird", "Other"));
            await db.SaveChangesAsync();
            secondId = second.Id;
            thirdId = third.Id;
        }
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings",
            new UpdateMetadataSettingsRequest { FetchEnabled = true, AcceptedConsentVersion = MetadataConsent.CurrentVersion })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/libraries/fnlib4", new UpdateMetadataLibraryRequest { FetchEnabled = true })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/libraries/fnlib5", new UpdateMetadataLibraryRequest { FetchEnabled = true })).EnsureSuccessStatusCode();
        recorder.Clear();

        // Linking a record that is not stored yet fetches and stores it first (a record may be linked from several libraries).
        var link = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/fnNetSeries/link",
            new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = MuFixtures.BerserkId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        link.EnsureSuccessStatusCode();
        var nudges = recorder.Nudges.ToList();
        var stored = nudges.IndexOf(N(libraryId, FacetNudgeReason.RecordStored));
        Assert.True(stored >= 0, "no RecordStored nudge for the library the admin acts in");
        Assert.True(nudges.IndexOf(N(libraryId, FacetNudgeReason.LinkChanged)) > stored, "the link nudge comes after the record's");
        Assert.DoesNotContain(nudges, n => n.LibraryId is null); // never every library (1.45.1, B2)

        // The second library links the same series (no network: the record is stored now).
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/fnNetSecond/link",
            new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = MuFixtures.BerserkId.ToString(System.Globalization.CultureInfo.InvariantCulture) })).StatusCode);

        recorder.Clear();
        (await admin.PostAsync("/api/v1/admin/metadata/nodes/fnNetSeries/refresh", null)).EnsureSuccessStatusCode();
        Assert.Equal(new[] { N(libraryId, FacetNudgeReason.RecordStored), N(secondId, FacetNudgeReason.RecordStored) },
            recorder.Nudges.OrderBy(n => n.LibraryId).ToArray());
        Assert.DoesNotContain(recorder.Nudges, n => n.LibraryId == thirdId || n.LibraryId is null);
        Assert.DoesNotContain(recorder.Nudges, n => n.Reason == FacetNudgeReason.LinkChanged); // a refresh writes no link
    }

    [Fact]
    public void TheApp_RegistersTheBuildersQueue_AndEveryCallSiteClassGetsTheRegisteredOne()
    {
        // 1.45.0 lane F: the builder's queue replaced the seam's no-op as the app's nudge.
        using (var plain = new ApiTokenTestFactory())
        {
            var nudge = plain.Services.GetRequiredService<IFacetIndexNudge>();
            Assert.IsType<com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Index.FacetIndexQueue>(nudge);
            Assert.Same(plain.Services.GetRequiredService<com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Index.FacetIndexQueue>(), nudge);
        }

        var recorder = new RecordingFacetIndexNudge();
        using var factory = Factory(recorder);
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        foreach (var service in new object[]
        {
            sp.GetRequiredService<MetadataLinkService>(),
            sp.GetRequiredService<MetadataIdentifyService>(),
            sp.GetRequiredService<DeclaredFactsService>(),
            sp.GetRequiredService<AuthorAliasLookupService>(),
            sp.GetRequiredService<ComicInfoBackfillService>(),
        })
        {
            var field = service.GetType().GetField("_facetNudge", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.Same(recorder, field.GetValue(service));
        }
    }

    [Fact]
    public async Task TheComicInfoBackfill_NudgesAfterEachBatchThatStoredRows_Only()
    {
        // 120 candidates in batches of 50: batch 1 stores, batch 2 only skips, batch 3 stores one. 1.45.1 (B2): each nudge names the
        // libraries of the archives the batch stored (1-60 in library 7, 61-120 in library 8) - never every library.
        var candidates = Enumerable.Range(1, 120).Select(i => new ComicInfoBackfillCandidate(i, 1, 0, 0, "/r", $"a{i}", i <= 60 ? 7 : 8)).ToList();
        var nudges = new List<long[]>();
        var counts = await ComicInfoBackfillService.RunPassCoreAsync(
            (after, limit, _) => Task.FromResult<IReadOnlyList<ComicInfoBackfillCandidate>>(candidates.Where(c => c.NodeId > after).Take(limit).ToList()),
            (c, _) => Task.FromResult(c.NodeId <= 50 || c.NodeId == 120 ? ComicInfoItemResult.Stored : ComicInfoItemResult.Skipped),
            () => false,
            () => 0,
            backoffMs: 1,
            CancellationToken.None,
            libraries => nudges.Add(libraries.Order().ToArray()));

        Assert.Equal(51, counts.Stored);
        Assert.Equal(new[] { new long[] { 7 }, new long[] { 8 } }, nudges);
    }
}
