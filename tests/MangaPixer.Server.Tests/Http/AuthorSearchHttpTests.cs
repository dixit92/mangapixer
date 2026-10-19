namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Authors;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using com.lifepixer.mangapixer.Tests.Server.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory, every provider client ending in the scripted handler) for author search by name (1.45.0, lane A):
/// <c>POST admin/metadata/nodes/{id}/author-search</c> sends exactly <c>{search, page: 1, perpage: 10}</c> to MangaUpdates' author search
/// and answers the candidates; it is refused with no request on an instance whose consent is older than the current one (also for an
/// answer still cached), when a switch is off, for bad text, for an archive; it is admin only with CSRF. The pick -
/// <c>PUT nodes/{id}/artist-folder</c> with <c>provider</c> + <c>externalId</c> - stores the reference and sends nothing; the reference
/// survives a declared-facts edit and "Delete fetched web data", makes the author eligible for "Artists' other names", and keeps its stored
/// author record through a per-library purge. "Match folders by name" as artist folders offers the search text and applies a pick.
/// Synthetic names only.
/// </summary>
[Trait("Category", "Http")]
public sealed class AuthorSearchHttpTests
{
    private const string LibPub = "aslib1";

    /// <summary>A hand-written author-search answer (the shape of MangaUpdates' <c>AuthorsSearchResponseV1</c>; synthetic names).</summary>
    private const string SearchAnswer = """
        {"total_hits": 3, "page": 1, "per_page": 10, "results": [
          {"record": {"id": 4815162342, "name": "Beta Painter", "url": "https://www.mangaupdates.com/author/abc123/beta-painter",
                      "genres": [{"genre": "Drama", "color": "#aaa"}, {"genre": "Romance"}, {"genre": "Comedy"}, {"genre": "Drama"}],
                      "stats": {"total_series": 12}},
           "hit_name": "Beta Painter"},
          {"record": {"id": 1234567, "name": "Painter Beta", "url": "http://evil.example/author",
                      "genres": ["Action"], "stats": {"total_series": 2}},
           "hit_name": "B. Painter"},
          {"record": {"id": 0, "name": "Broken"}},
          {"record": {"id": 99, "name": "  "}}
        ]}
        """;

    private static async Task SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Search Lib", RootPath = "/synthetic/as", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var folder = Node("asFolder", lib.Id, null, CatalogNodeKind.Folder, "Beta Painter");
        var other = Node("asOther", lib.Id, null, CatalogNodeKind.Folder, "Gamma Inker");
        db.CatalogNodes.AddRange(folder, other);
        await db.SaveChangesAsync();
        db.CatalogNodes.Add(Node("asArc", lib.Id, folder.Id, CatalogNodeKind.Archive, "Qzv Harbor Tale.cbz"));
        await db.SaveChangesAsync();
    }

    private static CatalogNodeEntity Node(string publicId, long libraryId, long? parentId, CatalogNodeKind kind, string name) => new()
    {
        PublicId = publicId,
        LibraryId = libraryId,
        ParentId = parentId,
        Kind = (int)kind,
        DisplayName = name,
        RelativePath = publicId,
        PathKey = publicId,
        SortKey = "0" + name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task EnableAsync(HttpClient admin)
    {
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings",
            new UpdateMetadataSettingsRequest { FetchEnabled = true, AcceptedConsentVersion = MetadataConsent.CurrentVersion })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}", new UpdateMetadataLibraryRequest { FetchEnabled = true }))
            .EnsureSuccessStatusCode();
    }

    private static void AnswerSearches(MetadataNetworkWebApplicationFactory factory) =>
        factory.Handler.Respond = request => request.RequestUri!.AbsolutePath == "/v1/authors/search" ? ScriptedHandler.Json(SearchAnswer) : null!;

    private static Task<HttpResponseMessage> SearchAsync(HttpClient client, string nodeId, string? text) =>
        client.PostAsJsonAsync($"/api/v1/admin/metadata/nodes/{nodeId}/author-search", new AuthorSearchRequest { Text = text });

    private static async Task<string?> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))?.Error;

    [Fact]
    public async Task Search_SendsExactlyTheFixedBody_AnswersTheCandidates_AndServesTheSameTextFromTheCache()
    {
        var sink = new CollectingSink();
        using var factory = new MetadataNetworkWebApplicationFactory(sink: sink);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin);
        AnswerSearches(factory);

        var response = await SearchAsync(admin, "asFolder", "  Beta   Painter ");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<AuthorSearchResultDto>(TestJson.Web))!;

        // (5) Exactly one request: POST /v1/authors/search with the confirmed text (whitespace collapsed), page 1, 10 per page - nothing else.
        var sent = Assert.Single(factory.Handler.Seen);
        Assert.Equal((HttpMethod.Post, "api.mangaupdates.com", "/v1/authors/search", ""), (sent.Method, sent.Uri.Host, sent.Uri.AbsolutePath, sent.Uri.Query));
        Assert.Equal("""{"search":"Beta Painter","page":1,"perpage":10}""", sent.Body);
        Assert.StartsWith("MangaPixer/", sent.Headers["User-Agent"], StringComparison.Ordinal);

        Assert.Equal(3, result.Total);
        Assert.Equal(2, result.Candidates.Count); // id 0 and an empty name are skipped
        var first = result.Candidates[0];
        Assert.Equal(("mangaupdates", "4815162342", "Beta Painter", (string?)null, (int?)12), (first.Provider, first.ExternalId, first.Name, first.HitName, first.TotalSeries));
        Assert.Equal(["Drama", "Romance", "Comedy"], first.Genres);
        Assert.Equal("https://www.mangaupdates.com/author/abc123/beta-painter", first.SiteUrl);
        var second = result.Candidates[1];
        Assert.Equal(("1234567", "B. Painter", (string?)null), (second.ExternalId, second.HitName, second.SiteUrl)); // not MangaUpdates' site: dropped
        Assert.Equal(["Action"], second.Genres);

        // The same text (any case / spacing) within the hour: no second request.
        var again = await SearchAsync(admin, "asOther", "beta painter");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(2, (await again.Content.ReadFromJsonAsync<AuthorSearchResultDto>(TestJson.Web))!.Candidates.Count);
        Assert.Single(factory.Handler.Seen);

        // Nothing is stored from a search: no author record, no declared fact, no link.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            Assert.Equal((0, 0, 0), (await db.MetadataAuthors.CountAsync(), await db.DeclaredFacts.CountAsync(), await db.NodeSeriesLinks.CountAsync()));
        }

        // Logs: the gateway line and AuthorSearchServed (ids, counts, timings) - never the text or a name.
        Assert.Contains(sink.Events, e => e.MessageTemplate.Text.StartsWith("Author search for library", StringComparison.Ordinal));
        Assert.Contains(sink.Events, e => e.MessageTemplate.Text.StartsWith("Metadata {Provider} {Operation} ({Origin})", StringComparison.Ordinal)
            && e.Properties["Operation"].ToString() == "\"author_search\"" && e.Properties["Origin"].ToString().Contains("Interactive", StringComparison.Ordinal));
        foreach (var e in sink.Events)
        {
            var rendered = e.RenderMessage() + " " + string.Join(" ", e.Properties.Select(p => p.Value.ToString())) + " " + e.Exception;
            Assert.False(rendered.Contains("Painter", StringComparison.OrdinalIgnoreCase), "A name leaked into a log line: " + e.MessageTemplate.Text);
        }
    }

    [Fact]
    public async Task AnInstanceAtThePreviousConsent_IsRefusedWithMetadataDisabled_WithNoRequest_EvenForACachedText()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin);
        AnswerSearches(factory);
        Assert.Equal(HttpStatusCode.OK, (await SearchAsync(admin, "asFolder", "Beta Painter")).StatusCode);
        Assert.Single(factory.Handler.Seen);

        // (4) An instance that accepted the consent before author search existed (the version before the current one).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            await db.AppSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.MetadataConsentVersion, MetadataConsent.CurrentVersion - 1));
        }
        foreach (var text in new[] { "Beta Painter", "Gamma Inker" })
        {
            var refused = await SearchAsync(admin, "asFolder", text);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("metadata_disabled", await ErrorOf(refused));
        }
        Assert.Single(factory.Handler.Seen);

        // Accepting the current text makes it work again.
        await EnableAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await SearchAsync(admin, "asFolder", "Gamma Inker")).StatusCode);
        Assert.Equal(2, factory.Handler.Seen.Count);
    }

    [Fact]
    public async Task Refusals_BadText_AnArchive_AnUnknownNode_TheLibrarySwitch_AndTheProviderBackoff()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        // Fetch off: 409, nothing sent.
        var off = await SearchAsync(admin, "asFolder", "Beta Painter");
        Assert.Equal((HttpStatusCode.Conflict, "metadata_disabled"), (off.StatusCode, await ErrorOf(off)));

        await EnableAsync(admin);
        foreach (var text in new[] { null, "", "   ", new string('x', 201) })
        {
            var bad = await SearchAsync(admin, "asFolder", text);
            Assert.Equal((HttpStatusCode.BadRequest, "invalid_query"), (bad.StatusCode, await ErrorOf(bad)));
        }
        var archive = await SearchAsync(admin, "asArc", "Beta Painter");
        Assert.Equal((HttpStatusCode.BadRequest, "not_a_folder"), (archive.StatusCode, await ErrorOf(archive)));
        Assert.Equal(HttpStatusCode.NotFound, (await SearchAsync(admin, "nope", "Beta Painter")).StatusCode);

        (await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}", new UpdateMetadataLibraryRequest { FetchEnabled = false }))
            .EnsureSuccessStatusCode();
        var library = await SearchAsync(admin, "asFolder", "Beta Painter");
        Assert.Equal((HttpStatusCode.Conflict, "library_metadata_disabled"), (library.StatusCode, await ErrorOf(library)));
        Assert.Equal(0, factory.Handler.CallCount);

        // MangaUpdates asks to slow down: 503 provider_backoff with Retry-After, and the next search sends nothing.
        await EnableAsync(admin);
        factory.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var limited = await SearchAsync(admin, "asFolder", "Beta Painter");
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "provider_backoff"), (limited.StatusCode, await ErrorOf(limited)));
        Assert.NotNull(limited.Headers.RetryAfter);
        var paused = await SearchAsync(admin, "asFolder", "Gamma Inker");
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "provider_backoff"), (paused.StatusCode, await ErrorOf(paused)));
        Assert.Equal(1, factory.Handler.CallCount);
    }

    [Fact]
    public async Task Search_IsAdminOnly_AndNeedsTheCsrfHeader()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        await SeedAsync(factory);
        var reader = await factory.CreateReaderClientAsync("asreader", LibPub);
        using var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await SearchAsync(reader, "asFolder", "Beta Painter")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SearchAsync(anon, "asFolder", "Beta Painter")).StatusCode);

        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin);
        var noCsrf = factory.CreateClient();
        (await noCsrf.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "admin", Password = "TestPassword123!" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await SearchAsync(noCsrf, "asFolder", "Beta Painter")).StatusCode);
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task ThePick_StoresTheReference_SendsNothing_AndSurvivesADeclaredEditAndDeleteFetchedWebData()
    {
        // Any outbound request fails the test: marking with a picked author must send nothing.
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin);

        foreach (var (provider, id) in new[] { ("anilist", "4815162342"), ("mangaupdates", "abc"), ("mangaupdates", "0123"), ("mangaupdates", null), (null, "4815162342") })
        {
            var bad = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/asFolder/artist-folder",
                new SetArtistFolderRequest { Name = "Beta Painter", Provider = provider, ExternalId = id });
            Assert.Equal((HttpStatusCode.BadRequest, "author_ref_invalid"), (bad.StatusCode, await ErrorOf(bad)));
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            Assert.Equal((0, 0), (await db.DeclaredFacts.CountAsync(), await db.NodeSeriesLinks.CountAsync())); // nothing changed on an error
        }

        var marked = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/asFolder/artist-folder",
            new SetArtistFolderRequest { Name = "Beta Painter", Role = "artist", Provider = "mangaupdates", ExternalId = "4815162342" });
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        var result = (await marked.Content.ReadFromJsonAsync<ArtistFolderResultDto>(TestJson.Web))!;
        Assert.Equal(("Beta Painter", "artist", "mangaupdates:4815162342"), (result.Artist.Name, result.Artist.Role, result.AuthorRef));
        Assert.Equal(SeriesLinkState.ArtistFolder, result.Change.Link?.State);
        Assert.Equal("mangaupdates:4815162342", await ReferenceAsync(factory, "asFolder"));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == "metadata.artist_folder");
            Assert.Equal(ArtistFolderAuditResult, audit.Result);
        }

        // Picking another author for the same folder replaces the reference; marking without one keeps it.
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/asFolder/artist-folder",
            new SetArtistFolderRequest { Name = "Beta Painter", Role = "artist", Provider = "mangaupdates", ExternalId = "1234567" })).EnsureSuccessStatusCode();
        Assert.Equal("mangaupdates:1234567", await ReferenceAsync(factory, "asFolder"));
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/asFolder/artist-folder",
            new SetArtistFolderRequest { Name = "Beta Painter", Role = "artist" })).EnsureSuccessStatusCode();
        Assert.Equal("mangaupdates:1234567", await ReferenceAsync(factory, "asFolder"));

        // A declared-facts edit that keeps the artist (adds another creator) keeps the reference.
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/asFolder/declared", new SetDeclaredFactsRequest
        {
            Creators = [new DeclaredCreatorDto { Name = "Beta Painter", Role = "artist" }, new DeclaredCreatorDto { Name = "Delta Writer", Role = "writer" }],
        })).EnsureSuccessStatusCode();
        Assert.Equal("mangaupdates:1234567", await ReferenceAsync(factory, "asFolder"));

        // "Delete fetched web data" (whole server, then the library) keeps the admin's decision: the mark and the reference.
        (await admin.PostAsJsonAsync("/api/v1/admin/metadata/purge", new MetadataPurgeRequest())).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/v1/admin/metadata/purge", new MetadataPurgeRequest { LibraryId = LibPub })).EnsureSuccessStatusCode();
        Assert.Equal("mangaupdates:1234567", await ReferenceAsync(factory, "asFolder"));
        Assert.Equal(0, factory.Handler.CallCount);
    }

    private const string ArtistFolderAuditResult = "author_search";

    private static async Task<string?> ReferenceAsync(MetadataNetworkWebApplicationFactory factory, string nodePublicId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var nodeId = await db.CatalogNodes.Where(n => n.PublicId == nodePublicId).Select(n => n.Id).SingleAsync();
        return await db.DeclaredFacts.AsNoTracking()
            .Where(f => f.NodeId == nodeId && f.Key == DeclaredFactKeys.Creator && f.ExternalRef != null)
            .Select(f => f.ExternalRef).SingleOrDefaultAsync();
    }

    [Fact]
    public async Task APickedAuthor_IsEligibleForArtistsOtherNames_AndItsRecordSurvivesALibraryPurge()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin);
        var miura = MuFixtures.MiuraId.ToString(CultureInfo.InvariantCulture);

        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/asFolder/artist-folder",
            new SetArtistFolderRequest { Name = "Beta Painter", Provider = "mangaupdates", ExternalId = miura })).EnsureSuccessStatusCode();
        Assert.Equal(0, factory.Handler.CallCount); // (Q3) the pick sends nothing

        var before = (await admin.GetFromJsonAsync<AuthorAliasStatusDto>("/api/v1/admin/metadata/authors", TestJson.Web))!;
        Assert.Equal((1, 0, 1), (before.Eligible, before.Fetched, before.ToFetch));

        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/v1/admin/metadata/authors/lookup", null)).StatusCode);
        await factory.Services.GetRequiredService<AuthorAliasLookupRunner>().Current.WaitAsync(TimeSpan.FromSeconds(30));
        var request = Assert.Single(factory.Handler.Seen);
        Assert.Equal((HttpMethod.Get, "/v1/authors/" + miura), (request.Method, request.Uri.AbsolutePath));
        var after = (await admin.GetFromJsonAsync<AuthorAliasStatusDto>("/api/v1/admin/metadata/authors", TestJson.Web))!;
        Assert.Equal((1, 1, 0, 1), (after.Eligible, after.Fetched, after.ToFetch, after.WithOtherNames));

        // A per-library purge removes author records no stored record names - but not one an admin's declared reference names.
        (await admin.PostAsJsonAsync("/api/v1/admin/metadata/purge", new MetadataPurgeRequest { LibraryId = LibPub })).EnsureSuccessStatusCode();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            Assert.Equal(miura, await db.MetadataAuthors.Select(a => a.ExternalId).SingleAsync());
        }

        // A library whose Fetch switch is off does not make its references eligible.
        (await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}", new UpdateMetadataLibraryRequest { FetchEnabled = false }))
            .EnsureSuccessStatusCode();
        Assert.Equal(0, (await admin.GetFromJsonAsync<AuthorAliasStatusDto>("/api/v1/admin/metadata/authors", TestJson.Web))!.Eligible);
    }

    [Fact]
    public async Task MatchFoldersByName_AsArtists_OffersTheSearchText_AndAppliesAPickWithItsReference()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        // asOther: an Artist folder without a reference yet - also offered; asFolder: no stored artist has its name.
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/asOther/artist-folder", new SetArtistFolderRequest())).EnsureSuccessStatusCode();
        var preview = await admin.PostAsJsonAsync("/api/v1/admin/metadata/folder-match/preview",
            new FolderMatchPreviewRequest { Kind = FolderMatchKind.Artists, NodeIds = ["asFolder", "asOther", "asArc"] });
        var rows = (await preview.Content.ReadFromJsonAsync<FolderMatchPreviewDto>(TestJson.Web))!.Rows.ToDictionary(r => r.NodeId);
        Assert.Equal((FolderMatchStatus.NoMatch, "Beta Painter"), (rows["asFolder"].Status, rows["asFolder"].SearchText));
        Assert.Equal((FolderMatchStatus.Decided, "Gamma Inker"), (rows["asOther"].Status, rows["asOther"].SearchText));
        Assert.Null(rows["asArc"].SearchText);

        var applied = await admin.PostAsJsonAsync("/api/v1/admin/metadata/folder-match/apply", new FolderMatchApplyRequest
        {
            Kind = FolderMatchKind.Artists,
            Items =
            [
                new FolderMatchApplyItem { NodeId = "asFolder", Name = "Beta Painter", Role = "author", Provider = "mangaupdates", ExternalId = "4815162342" },
                new FolderMatchApplyItem { NodeId = "asOther", Name = "Gamma Inker", Role = "author", Provider = "mangaupdates", ExternalId = "x1" },
            ],
        });
        var result = (await applied.Content.ReadFromJsonAsync<FolderMatchApplyResultDto>(TestJson.Web))!;
        Assert.Equal(["ok", "author_ref_invalid"], result.Results.Select(r => r.Code).ToArray());
        Assert.Equal("mangaupdates:4815162342", await ReferenceAsync(factory, "asFolder"));
        Assert.Null(await ReferenceAsync(factory, "asOther"));

        // Once referenced, an Artist folder is no longer offered the search.
        var again = await admin.PostAsJsonAsync("/api/v1/admin/metadata/folder-match/preview",
            new FolderMatchPreviewRequest { Kind = FolderMatchKind.Artists, NodeIds = ["asFolder", "asOther"] });
        var rows2 = (await again.Content.ReadFromJsonAsync<FolderMatchPreviewDto>(TestJson.Web))!.Rows.ToDictionary(r => r.NodeId);
        Assert.Equal((FolderMatchStatus.Decided, (string?)null), (rows2["asFolder"].Status, rows2["asFolder"].SearchText));
        Assert.Equal("Gamma Inker", rows2["asOther"].SearchText);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var nodeId = await db.CatalogNodes.Where(n => n.PublicId == "asFolder").Select(n => n.Id).SingleAsync();
            var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == "metadata.artist_folder" && a.TargetItemId == nodeId);
            Assert.Equal(ArtistFolderAuditResult, audit.Result);
        }
        Assert.Equal(0, factory.Handler.CallCount);
    }
}
