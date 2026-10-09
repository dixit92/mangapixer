namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Authors;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory, every provider client ending in the scripted handler) for artists' other names (1.38.0, lane U):
/// the admin endpoints start / status / cancel the look-up of MangaUpdates author records; the run is reachable through them only,
/// sends <c>GET /v1/authors/{id}</c> for the ids on the linked Berserk record and nothing else, stores the answers, and the registered
/// <see cref="IAuthorAliasSource"/> answers from them. An instance at consent 4 is refused with no request; readers and anonymous
/// callers are refused; a POST without the CSRF header is refused.
/// </summary>
public sealed class AuthorAliasHttpTests
{
    private const string LibPub = "aulib1";
    private static readonly string BerserkId = MuFixtures.BerserkId.ToString(CultureInfo.InvariantCulture);

    private static async Task SeedFolderAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Author Lib", RootPath = "/synthetic/au", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "auSeries",
            LibraryId = lib.Id,
            Kind = 0,
            DisplayName = "Berserk",
            RelativePath = "auSeries",
            PathKey = "auSeries",
            SortKey = "0Berserk",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task EnableAsync(HttpClient admin)
    {
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings",
            new UpdateMetadataSettingsRequest { FetchEnabled = true, AcceptedConsentVersion = MetadataConsent.CurrentVersion })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}", new UpdateMetadataLibraryRequest { FetchEnabled = true }))
            .EnsureSuccessStatusCode();
    }

    private static List<SeenRequest> AuthorRequests(MetadataNetworkWebApplicationFactory factory) =>
        factory.Handler.Seen.Where(r => r.Uri.AbsolutePath.StartsWith("/v1/authors/", StringComparison.Ordinal)).ToList();

    private static async Task<AuthorAliasStatusDto> StatusAsync(HttpClient admin) =>
        (await admin.GetFromJsonAsync<AuthorAliasStatusDto>("/api/v1/admin/metadata/authors", TestJson.Web))!;

    [Fact]
    public async Task LookUp_FetchesTheLinkedRecordsAuthors_StoresThem_AndTheAliasSourceAnswers()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        await SeedFolderAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin);
        // Linking fetches the Berserk record (one series GET) - its creators name three MangaUpdates author ids.
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/auSeries/link", new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = BerserkId }))
            .EnsureSuccessStatusCode();
        Assert.Empty(AuthorRequests(factory)); // linking never reads author records

        var before = await StatusAsync(admin);
        Assert.Equal((3, 0, 3, 0), (before.Eligible, before.Fetched, before.ToFetch, before.WithOtherNames));
        Assert.Equal((180, 1), (before.RefreshAfterDays, before.SecondsPerRequest));
        Assert.Null(before.BlockedReason);
        Assert.Null(before.Running);

        var start = await admin.PostAsync("/api/v1/admin/metadata/authors/lookup", null);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var started = (await start.Content.ReadFromJsonAsync<AuthorAliasStatusDto>(TestJson.Web))!;
        Assert.Equal(3, started.Running?.Total);
        await factory.Services.GetRequiredService<AuthorAliasLookupRunner>().Current.WaitAsync(TimeSpan.FromSeconds(30));

        var after = await StatusAsync(admin);
        Assert.Equal((3, 3, 0, 2), (after.Eligible, after.Fetched, after.ToFetch, after.WithOtherNames));
        Assert.Null(after.Running);
        Assert.Equal(("completed", 3, 3, 2, 1, 0), (after.LastRun!.Outcome, after.LastRun.Total, after.LastRun.Requests, after.LastRun.Stored,
            after.LastRun.NotFound, after.LastRun.Failed));

        // Exactly the three author ids of the stored record: numeric ids in the path, no body, nothing else.
        Assert.Equal(["/v1/authors/22635311083", "/v1/authors/38824888050", "/v1/authors/58953789514"],
            AuthorRequests(factory).Select(r => r.Uri.AbsolutePath).ToArray());
        Assert.All(AuthorRequests(factory), r => Assert.Equal((HttpMethod.Get, (string?)null, ""), (r.Method, r.Body, r.Uri.Query)));

        using (var scope = factory.Services.CreateScope())
        {
            var source = scope.ServiceProvider.GetRequiredService<IAuthorAliasSource>();
            Assert.IsType<StoredAuthorAliases>(source);
            var found = await source.GetAsync(["22635311083", "58953789514"], CancellationToken.None);
            var miura = Assert.Single(found).Value;
            Assert.Equal("MIURA Kentaro", miura.Name);
            Assert.Contains("Kentaro Miura", miura.OtherNames);

            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == "metadata.authors_fetch");
            Assert.Equal("ids_3", audit.Result);
        }

        // Nothing left: a second start sends nothing.
        var again = await admin.PostAsync("/api/v1/admin/metadata/authors/lookup", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(3, AuthorRequests(factory).Count);

        // "Delete fetched web data" removes the stored author records with the records.
        (await admin.PostAsJsonAsync("/api/v1/admin/metadata/purge", new MetadataPurgeRequest())).EnsureSuccessStatusCode();
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().MetadataAuthors.CountAsync());
    }

    [Fact]
    public async Task Cancel_StopsTheRunningLookUp_AndTheRestStaysUnfetched()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        await SeedFolderAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin);
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/auSeries/link", new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = BerserkId }))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/v1/admin/metadata/authors/lookup", null)).StatusCode);
        var cancel = await admin.PostAsync("/api/v1/admin/metadata/authors/lookup/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        await factory.Services.GetRequiredService<AuthorAliasLookupRunner>().Current.WaitAsync(TimeSpan.FromSeconds(30));

        var status = await StatusAsync(admin);
        Assert.Equal("cancelled", status.LastRun!.Outcome);
        Assert.True(AuthorRequests(factory).Count < 3, $"{AuthorRequests(factory).Count} requests");
        Assert.True(status.ToFetch > 0);
        Assert.Equal(3, status.Fetched + status.ToFetch);
    }

    [Fact]
    public async Task AnInstanceAtConsent4_IsRefused_WithNoRequest_UntilAnAdminAccepts5()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        await SeedFolderAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin);
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/auSeries/link", new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = BerserkId }))
            .EnsureSuccessStatusCode();
        using (var scope = factory.Services.CreateScope())
        {
            // An instance that accepted the 1.32.0 text (4) before this update.
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            await db.AppSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.MetadataConsentVersion, 4));
        }
        var calls = factory.Handler.CallCount;

        var refused = await admin.PostAsync("/api/v1/admin/metadata/authors/lookup", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("metadata_disabled", (await refused.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        Assert.Equal("metadata_disabled", (await StatusAsync(admin)).BlockedReason);
        Assert.True((await admin.GetFromJsonAsync<MetadataSettingsDto>("/api/v1/admin/metadata/settings", TestJson.Web))!.ConsentRenewalNeeded);
        Assert.Equal(calls, factory.Handler.CallCount);
        Assert.Null(factory.Services.GetRequiredService<AuthorAliasLookupRunner>().LastRun);

        await EnableAsync(admin); // accepts 5
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/v1/admin/metadata/authors/lookup", null)).StatusCode);
        await factory.Services.GetRequiredService<AuthorAliasLookupRunner>().Current.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(3, AuthorRequests(factory).Count);
    }

    [Fact]
    public async Task Endpoints_AreAdminOnly_AndPostsNeedTheCsrfHeader()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        await SeedFolderAsync(factory);
        var reader = await factory.CreateReaderClientAsync("aureader", LibPub);
        using var anon = factory.CreateClient();

        foreach (var client in new[] { reader, anon })
        {
            var expected = client == anon ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            Assert.Equal(expected, (await client.GetAsync("/api/v1/admin/metadata/authors")).StatusCode);
            Assert.Equal(expected, (await client.PostAsync("/api/v1/admin/metadata/authors/lookup", null)).StatusCode);
            Assert.Equal(expected, (await client.PostAsync("/api/v1/admin/metadata/authors/lookup/cancel", null)).StatusCode);
        }

        // An admin session without the anti-forgery header cannot start a look-up.
        await factory.LoginAsAdminWithChangedPasswordAsync();
        var noCsrf = factory.CreateClient();
        (await noCsrf.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "admin", Password = "TestPassword123!" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await noCsrf.PostAsync("/api/v1/admin/metadata/authors/lookup", null)).StatusCode);

        Assert.Equal(0, factory.Handler.CallCount);
        Assert.Null(factory.Services.GetRequiredService<AuthorAliasLookupRunner>().LastRun);
    }
}
