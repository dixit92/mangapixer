namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Authors;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.FolderMatch;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory, one host and SQLite DB per test) for "Match folders by name" (1.38.0):
/// <c>POST admin/metadata/folder-match/preview</c> and <c>/apply</c> - admin only, CSRF, 1-200 nodes; artists grouped by the MangaUpdates
/// author id with the main name declared, the stored author records' other names (a fake <see cref="IAuthorAliasSource"/>; the real one is
/// empty), GCD names; collections by title / alt title of stored MangaUpdates / GCD records (linked series first); decided folders
/// unticked; and the apply through the single actions with stored records only. Every host fails on any outgoing request: nothing may be
/// sent by either route. Synthetic names.
/// </summary>
[Trait("Category", "Http")]
public sealed class FolderMatchHttpTests
{
    private const string Lib = "fmlib1";
    private const string Preview = "/api/v1/admin/metadata/folder-match/preview";
    private const string Apply = "/api/v1/admin/metadata/folder-match/apply";

    /// <summary>Author 502's stored record: main name "Second Inker Main", other name "Pen Alias".</summary>
    private sealed class FakeAliases : IAuthorAliasSource
    {
        public List<string> Asked { get; } = [];

        public Task<IReadOnlyDictionary<string, AuthorAliases>> GetAsync(IReadOnlyCollection<string> authorIds, CancellationToken ct)
        {
            Asked.AddRange(authorIds);
            IReadOnlyDictionary<string, AuthorAliases> result = authorIds.Contains("502")
                ? new Dictionary<string, AuthorAliases> { ["502"] = new("502", "Second Inker Main", ["Pen Alias"]) }
                : new Dictionary<string, AuthorAliases>();
            return Task.FromResult(result);
        }
    }

    private static MetadataNetworkWebApplicationFactory NewFactory(IAuthorAliasSource? aliases = null) => aliases is { } source
        ? new(failOnAnyRequest: true, configureServices: s => s.AddScoped(_ => source))
        : new(failOnAnyRequest: true);

    // Folders: fmA1 "Qzv Painter", fmA2 "Painter Qzv", fmA3 "Shared Name", fmA4 "Nobody Known", fmA5 "Second Inker" (Don't match),
    // fmA6 "Pen Alias", fmA7 "Comic Writer"; fmC1 "Tsuki no Niwa (Doujinshi)" (+ archive fmArc), fmC2 "Harbor Lights",
    // fmC3 "Unknown Series Name [Scans]", fmC4 "Paper Moon", fmC5 "Lantern Road" (Needs review); fmSeries "Moonlit Garden" (Confirmed r1),
    // fmSeries2 "Harbor Lights Again" (Auto r3).
    private static async Task SeedAsync(IServiceProvider services, bool metadataEnabled = false)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var now = DateTimeOffset.UtcNow;
        var lib = new LibraryEntity
        {
            PublicId = Lib,
            DisplayName = "Folder Match",
            RootPath = "/synthetic/" + Lib,
            CreatedAt = now,
            MetadataEnabled = metadataEnabled,
        };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();

        var folders = new Dictionary<string, CatalogNodeEntity>();
        foreach (var (pub, name) in new[]
        {
            ("fmA1", "Qzv Painter"), ("fmA2", "Painter Qzv"), ("fmA3", "Shared Name"), ("fmA4", "Nobody Known"), ("fmA5", "Second Inker"),
            ("fmA6", "Pen Alias"), ("fmA7", "Comic Writer"), ("fmC1", "Tsuki no Niwa (Doujinshi)"), ("fmC2", "Harbor Lights"),
            ("fmC3", "Unknown Series Name [Scans]"), ("fmC4", "Paper Moon"), ("fmC5", "Lantern Road"), ("fmSeries", "Moonlit Garden"),
            ("fmSeries2", "Harbor Lights Again"),
        })
        {
            folders[pub] = Node(pub, lib.Id, null, CatalogNodeKind.Folder, name);
        }
        db.CatalogNodes.AddRange(folders.Values);
        await db.SaveChangesAsync();
        db.CatalogNodes.AddRange(
            Node("fmArc", lib.Id, folders["fmC1"].Id, CatalogNodeKind.Archive, "Qzv Short Story.cbz"),
            Node("fmA1Arc", lib.Id, folders["fmA1"].Id, CatalogNodeKind.Archive, "Qzv First Work.cbz"),
            Node("fmC4Arc1", lib.Id, folders["fmC4"].Id, CatalogNodeKind.Archive, "Qzv Fan Work One.cbz"),
            Node("fmC4Arc2", lib.Id, folders["fmC4"].Id, CatalogNodeKind.Archive, "Qzv Fan Work Two.cbz"));

        static string Creators(params (string Name, string Role, string? Id)[] c) =>
            MetadataJson.WriteList(c.Select(x => new MetadataJson.Creator(x.Name, x.Role, x.Id)).ToList())!;
        var r1 = Record("mangaupdates", "1001", "Moonlit Garden", ["Tsuki no Niwa"], Creators(("Qzv Painter", "author", "501")), now);
        var r2 = Record("mangaupdates", "1002", "Harbor Lights", [],
            Creators(("Painter Qzv", "artist", "501"), ("Second Inker", "artist", "502")), now.AddDays(-2));
        var r3 = Record("mangaupdates", "1003", "Harbor Lights", [], Creators(("Qzv Painter", "author", "501")), now.AddDays(-1));
        var r4 = Record("gcd", "2001", "Steel Hero", [], Creators(("Comic Writer", "author", null)), now);
        var r5 = Record("anilist", "3001", "Paper Moon", [], Creators(("Not An Artist Here", "author", null)), now);
        var r6 = Record("mangaupdates", "1006", "Paper Moon", [], Creators(("Shared Name", "author", "601")), now);
        r6.ImageRemoteUrl = "https://cdn.mangaupdates.com/image/synthetic.jpg"; // poster not stored: the apply must not download it
        var r7 = Record("mangaupdates", "1007", "Other Work A", [], Creators(("Other Person", "artist", "602")), now);
        var r8 = Record("mangaupdates", "1008", "Other Work B", [], Creators(("Other Person", "artist", "602")), now);
        var r9 = Record("mangaupdates", "1009", "Other Work C", [], Creators(("Shared Name", "artist", "602")), now);
        db.MetadataRecords.AddRange(r1, r2, r3, r4, r5, r6, r7, r8, r9);
        await db.SaveChangesAsync();

        void Link(string pub, SeriesLinkState state, MetadataRecordEntity? record) => db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = folders[pub].Id,
            LibraryId = lib.Id,
            State = (int)state,
            RecordId = record?.Id,
            MatchMethod = (int)MetadataMatchMethod.Auto,
            CreatedAt = now,
            UpdatedAt = now,
        });
        Link("fmSeries", SeriesLinkState.Confirmed, r1);
        Link("fmSeries2", SeriesLinkState.Auto, r3);
        Link("fmA5", SeriesLinkState.DontMatch, null);
        Link("fmC5", SeriesLinkState.NeedsReview, null);
        await db.SaveChangesAsync();
    }

    private static int s_record;

    private static MetadataRecordEntity Record(string provider, string id, string title, string[] alt, string creators, DateTimeOffset fetched) => new()
    {
        PublicId = "fmr" + Interlocked.Increment(ref s_record).ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
        Provider = provider,
        ExternalId = id,
        Title = title,
        AltTitlesJson = alt.Length == 0 ? null : MetadataJson.WriteList(alt),
        CreatorsJson = creators,
        StartYear = 2001,
        ProviderType = "Manga",
        FetchedAt = fetched,
    };

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, CatalogNodeKind kind, string name) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = (int)kind,
        DisplayName = name,
        RelativePath = pub,
        PathKey = pub,
        SortKey = (kind == CatalogNodeKind.Folder ? "0" : "1") + name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<FolderMatchPreviewDto> PreviewAsync(HttpClient admin, FolderMatchKind kind, params string[] ids)
    {
        var response = await admin.PostAsJsonAsync(Preview, new FolderMatchPreviewRequest { Kind = kind, NodeIds = ids });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FolderMatchPreviewDto>(TestJson.Web))!;
    }

    private static async Task<FolderMatchApplyResultDto> ApplyAsync(HttpClient admin, FolderMatchApplyRequest request)
    {
        var response = await admin.PostAsJsonAsync(Apply, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FolderMatchApplyResultDto>(TestJson.Web))!;
    }

    private static async Task<Dictionary<string, (SeriesLinkState State, string? ExternalId)>> LinksAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await (from l in db.NodeSeriesLinks
                      join n in db.CatalogNodes on l.NodeId equals n.Id
                      join r in db.MetadataRecords on l.RecordId equals (long?)r.Id into rs
                      from r in rs.DefaultIfEmpty()
                      select new { n.PublicId, l.State, ExternalId = r == null ? null : r.ExternalId })
            .ToDictionaryAsync(x => x.PublicId, x => ((SeriesLinkState)x.State, (string?)x.ExternalId));
    }

    [Fact]
    public async Task Service_IsRegistered_AndTheRoutesAreAdminOnlyWithCsrf()
    {
        using var factory = NewFactory();
        await SeedAsync(factory.Services);
        using (var scope = factory.Services.CreateScope())
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<FolderMatchService>());

        var reader = await factory.CreateReaderClientAsync("fmreader", Lib);
        var body = new FolderMatchPreviewRequest { Kind = FolderMatchKind.Artists, NodeIds = ["fmA1"] };
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Preview, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Apply, new FolderMatchApplyRequest
        {
            Kind = FolderMatchKind.Artists,
            Items = [new FolderMatchApplyItem { NodeId = "fmA1" }],
        })).StatusCode);

        var noCsrf = factory.CreateClient();
        (await noCsrf.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "admin", Password = "TestPassword123!" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await noCsrf.PostAsJsonAsync(Apply, new FolderMatchApplyRequest
        {
            Kind = FolderMatchKind.Artists,
            Items = [new FolderMatchApplyItem { NodeId = "fmA1" }],
        })).StatusCode);
        Assert.False((await LinksAsync(factory.Services)).ContainsKey("fmA1"));
    }

    [Fact]
    public async Task Preview_RefusesNoneOrMoreThan200Nodes()
    {
        using var factory = NewFactory();
        await SeedAsync(factory.Services);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var none = await admin.PostAsJsonAsync(Preview, new FolderMatchPreviewRequest { Kind = FolderMatchKind.Artists, NodeIds = [] });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("invalid_node_ids", (await none.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        var many = Enumerable.Range(0, 201).Select(i => "n" + i).ToList();
        var tooMany = await admin.PostAsJsonAsync(Preview, new FolderMatchPreviewRequest { Kind = FolderMatchKind.Artists, NodeIds = many });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        var exactly200 = await PreviewAsync(admin, FolderMatchKind.Artists, [.. many.Take(199), "fmA1"]);
        Assert.Equal(200, exactly200.Rows.Count);
        Assert.Equal(199, exactly200.Rows.Count(r => r.Status == FolderMatchStatus.NotFound));
    }

    [Fact]
    public async Task PreviewArtists_GroupsByAuthorId_DeclaresTheMainName_AndShowsAmbiguousDecidedAndNoMatch_SendingNothing()
    {
        using var factory = NewFactory();
        await SeedAsync(factory.Services);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var preview = await PreviewAsync(admin, FolderMatchKind.Artists, "fmA1", "fmA2", "fmA3", "fmA4", "fmA5", "fmA6", "fmA7", "fmArc", "nope");
        var rows = preview.Rows.ToDictionary(r => r.NodeId);
        Assert.Equal(["fmA1", "fmA2", "fmA3", "fmA4", "fmA5", "fmA6", "fmA7", "fmArc", "nope"], preview.Rows.Select(r => r.NodeId));

        // Author 501 is spelled "Qzv Painter" on two records and "Painter Qzv" on one: one artist, the main spelling declared, "Story & art".
        var a1 = Assert.Single(rows["fmA1"].Artists);
        Assert.Equal((FolderMatchStatus.Proposed, "Qzv Painter", "author", "Qzv Painter", 3), (rows["fmA1"].Status, a1.Name, a1.Role, a1.MatchedName, a1.RecordCount));
        var a2 = Assert.Single(rows["fmA2"].Artists);
        Assert.Equal((FolderMatchStatus.Proposed, "Qzv Painter", "Painter Qzv"), (rows["fmA2"].Status, a2.Name, a2.MatchedName));

        // "Shared Name": author 601 (its only name) and author 602 (main name "Other Person", on more records).
        Assert.Equal(FolderMatchStatus.Ambiguous, rows["fmA3"].Status);
        Assert.Equal([("Shared Name", "author"), ("Other Person", "artist")], rows["fmA3"].Artists.Select(a => (a.Name, a.Role)));

        Assert.Equal(FolderMatchStatus.NoMatch, rows["fmA4"].Status);
        Assert.Empty(rows["fmA4"].Artists);
        // A Don't match folder: decided (unticked), its match still shown.
        Assert.Equal((FolderMatchStatus.Decided, SeriesLinkState.DontMatch), (rows["fmA5"].Status, rows["fmA5"].CurrentState));
        Assert.Equal("Second Inker", Assert.Single(rows["fmA5"].Artists).Name);
        // No stored author records (the real alias source is empty until they are fetched): an alias matches nothing.
        Assert.Equal(FolderMatchStatus.NoMatch, rows["fmA6"].Status);
        // A GCD creator (no id) is an artist of its own.
        var gcd = Assert.Single(rows["fmA7"].Artists);
        Assert.Equal(("Comic Writer", "gcd", "author"), (gcd.Name, gcd.Provider, gcd.Role));
        // The AniList record's creator is not an artist here.
        Assert.Equal(FolderMatchStatus.NotAFolder, rows["fmArc"].Status);
        Assert.Equal(FolderMatchStatus.NotFound, rows["nope"].Status);
        Assert.Null(rows["fmA1"].SearchText);
        Assert.True(preview.Compared >= 5);

        Assert.Equal(0, factory.Handler.CallCount);
        Assert.False((await LinksAsync(factory.Services)).ContainsKey("fmA1")); // the preview changes nothing
    }

    [Fact]
    public async Task PreviewArtists_MatchesTheStoredAuthorRecordsOtherNames_AndDeclaresItsMainName()
    {
        var aliases = new FakeAliases();
        using var factory = NewFactory(aliases);
        await SeedAsync(factory.Services);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var preview = await PreviewAsync(admin, FolderMatchKind.Artists, "fmA6", "fmA5");
        var alias = Assert.Single(preview.Rows[0].Artists);
        Assert.Equal((FolderMatchStatus.Proposed, "Second Inker Main", "Pen Alias", "artist"),
            (preview.Rows[0].Status, alias.Name, alias.MatchedName, alias.Role));
        Assert.Equal("Second Inker Main", Assert.Single(preview.Rows[1].Artists).Name);
        // Only MangaUpdates author ids from stored records are asked for (no GCD names, no AniList records).
        Assert.Equal(["501", "502", "601", "602"], aliases.Asked.Order(StringComparer.Ordinal));
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task PreviewCollections_MatchesTitlesAndAltTitles_LinkedSeriesFirst_WithTheSearchTextForTheRest()
    {
        using var factory = NewFactory();
        await SeedAsync(factory.Services);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var preview = await PreviewAsync(admin, FolderMatchKind.Collections, "fmC1", "fmC2", "fmC3", "fmC4", "fmC5", "fmSeries", "fmA7");
        var rows = preview.Rows.ToDictionary(r => r.NodeId);

        var c1 = Assert.Single(rows["fmC1"].Records);
        Assert.Equal((FolderMatchStatus.Proposed, "1001", "Moonlit Garden", "Tsuki no Niwa", true),
            (rows["fmC1"].Status, c1.ExternalId, c1.Title, c1.MatchedTitle, c1.LinkedAsSeries));
        Assert.Null(rows["fmC1"].SearchText);

        // Two stored records titled "Harbor Lights": the one linked as a series here comes first.
        Assert.Equal(FolderMatchStatus.Ambiguous, rows["fmC2"].Status);
        Assert.Equal([("1003", true), ("1002", false)], rows["fmC2"].Records.Select(r => (r.ExternalId, r.LinkedAsSeries)));

        Assert.Equal(FolderMatchStatus.NoMatch, rows["fmC3"].Status);
        Assert.Equal("Unknown Series Name", rows["fmC3"].SearchText);

        // "Paper Moon": the MangaUpdates record only (an AniList record is never a collection's series).
        var c4 = Assert.Single(rows["fmC4"].Records);
        Assert.Equal(("mangaupdates", "1006", false), (c4.Provider, c4.ExternalId, c4.LinkedAsSeries));

        // A Needs-review row is not a decision; a Confirmed folder is (unticked).
        Assert.Equal(FolderMatchStatus.NoMatch, rows["fmC5"].Status);
        Assert.Equal(SeriesLinkState.NeedsReview, rows["fmC5"].CurrentState);
        Assert.Equal((FolderMatchStatus.Decided, SeriesLinkState.Confirmed), (rows["fmSeries"].Status, rows["fmSeries"].CurrentState));
        // A GCD record's title matches too ("Comic Writer" is no title: no match).
        Assert.Equal(FolderMatchStatus.NoMatch, rows["fmA7"].Status);
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task ApplyArtists_MarksEachFolderThroughTheSingleAction_WithPerRowErrors()
    {
        using var factory = NewFactory();
        await SeedAsync(factory.Services);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var result = await ApplyAsync(admin, new FolderMatchApplyRequest
        {
            Kind = FolderMatchKind.Artists,
            Items =
            [
                new FolderMatchApplyItem { NodeId = "fmA1", Name = "Qzv Painter", Role = "author" },
                new FolderMatchApplyItem { NodeId = "fmA3", Name = "Other Person", Role = "artist" },
                new FolderMatchApplyItem { NodeId = "fmA4" }, // "mark with the folder's own name"
                new FolderMatchApplyItem { NodeId = "fmArc", Name = "Qzv Painter" },
                new FolderMatchApplyItem { NodeId = "nope" },
                new FolderMatchApplyItem { NodeId = "fmA1", Name = "Again" },
                new FolderMatchApplyItem { NodeId = "fmA2", Role = "colorist" },
            ],
        });

        Assert.Equal((3, 4), (result.Succeeded, result.Failed));
        Assert.Equal(["ok", "ok", "ok", "not_a_folder", "not_found", "duplicate", "creator_role_invalid"], result.Results.Select(r => r.Code));
        var links = await LinksAsync(factory.Services);
        Assert.Equal(SeriesLinkState.ArtistFolder, links["fmA1"].State);
        Assert.Equal(SeriesLinkState.ArtistFolder, links["fmA3"].State);
        Assert.Equal(SeriesLinkState.ArtistFolder, links["fmA4"].State);
        Assert.False(links.ContainsKey("fmA2"));

        var declared = (await admin.GetFromJsonAsync<DeclaredFactsScopeDto>("/api/v1/admin/metadata/folders/fmA3/declared", TestJson.Web))!;
        Assert.Equal(("Other Person", "artist"), (declared.Own.Creators.Single().Name, declared.Own.Creators.Single().Role));
        var own = (await admin.GetFromJsonAsync<DeclaredFactsScopeDto>("/api/v1/admin/metadata/folders/fmA4/declared", TestJson.Web))!;
        Assert.Equal(("Nobody Known", "author"), (own.Own.Creators.Single().Name, own.Own.Creators.Single().Role));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var audits = await db.AuditEvents.Where(a => a.Action == AuditActions.MetadataArtistFolder).Select(a => a.Result).ToListAsync();
        Assert.Equal(3, audits.Count(r => r == FolderMatchService.AuditResult));
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task ApplyCollections_StoredRecordsOnly_NeverFetches_AndQueuesTheWorksInside()
    {
        using var factory = NewFactory();
        await SeedAsync(factory.Services, metadataEnabled: true);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest
        {
            FetchEnabled = true,
            AcceptedConsentVersion = MetadataConsent.CurrentVersion,
            AutoMatchEnabled = true,
            AcceptedAutoConsentVersion = MetadataAutoConsent.CurrentVersion,
        })).EnsureSuccessStatusCode();

        var result = await ApplyAsync(admin, new FolderMatchApplyRequest
        {
            Kind = FolderMatchKind.Collections,
            Items =
            [
                new FolderMatchApplyItem { NodeId = "fmC4", Provider = "mangaupdates", ExternalId = "1006" }, // poster not stored
                new FolderMatchApplyItem { NodeId = "fmC1", Provider = "mangaupdates", ExternalId = "1001" },
                new FolderMatchApplyItem { NodeId = "fmC3", Provider = "mangaupdates", ExternalId = "999999" }, // not stored: never fetched
                new FolderMatchApplyItem { NodeId = "fmC2", Provider = "anilist", ExternalId = "3001" },
                new FolderMatchApplyItem { NodeId = "fmC5" },
                new FolderMatchApplyItem { NodeId = "fmArc", Provider = "mangaupdates", ExternalId = "1001" },
            ],
        });

        Assert.Equal(["ok", "ok", "record_not_stored", "invalid_request", "invalid_request", "not_a_folder"], result.Results.Select(r => r.Code));
        Assert.Equal(2, result.Results[0].Queued); // the two works inside, queued at once
        var links = await LinksAsync(factory.Services);
        Assert.Equal((SeriesLinkState.CollectionAbout, "1006"), links["fmC4"]);
        Assert.Equal((SeriesLinkState.CollectionAbout, "1001"), links["fmC1"]);
        Assert.False(links.ContainsKey("fmC3"));
        Assert.Equal(SeriesLinkState.NeedsReview, links["fmC5"].State);

        var content = (await admin.GetFromJsonAsync<FolderMetadataContentDto>("/api/v1/admin/metadata/folders/fmC4/content", TestJson.Web))!;
        Assert.Equal(MetadataFolderContent.DoujinshiAndAdultOneShots, content.Effective);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            Assert.False(await db.MetadataRecords.AnyAsync(r => r.ExternalId == "999999"));
            Assert.Equal(0, await db.MetadataRecords.Where(r => r.ExternalId == "1006").Select(r => r.ImageState).SingleAsync());
            Assert.Equal(2, await db.AuditEvents.CountAsync(a => a.Action == AuditActions.MetadataCollection && a.Result == FolderMatchService.AuditResult));
        }
        Assert.Equal(0, factory.Handler.CallCount); // neither the record nor its poster was requested
    }

    [Fact]
    public async Task Apply_RefusesNoneOrMoreThan200Items()
    {
        using var factory = NewFactory();
        await SeedAsync(factory.Services);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var none = await admin.PostAsJsonAsync(Apply, new FolderMatchApplyRequest { Kind = FolderMatchKind.Artists, Items = [] });
        Assert.Equal("invalid_items", (await none.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        var tooMany = await admin.PostAsJsonAsync(Apply, new FolderMatchApplyRequest
        {
            Kind = FolderMatchKind.Artists,
            Items = Enumerable.Range(0, 201).Select(i => new FolderMatchApplyItem { NodeId = i == 0 ? "fmA1" : "n" + i }).ToList(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.False((await LinksAsync(factory.Services)).ContainsKey("fmA1")); // nothing applied
    }
}
