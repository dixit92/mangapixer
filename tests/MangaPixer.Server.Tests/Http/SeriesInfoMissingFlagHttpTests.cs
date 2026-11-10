namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) for <see cref="SeriesInfoDto.MissingReportPossible"/> (1.53.0, lane V - the 1.52.0 C3 residue): the
/// flag is true exactly when <c>GET nodes/{anchor}/missing</c> answers a row, for every state the 1.52.0 note lists - an own Confirmed /
/// Auto link, a link inherited from the folder above, ComicInfo only, Don't match, a collection, an artist folder, a link still in
/// review, an archive, nothing at all - in a library that shows series information AND in one that hides it (state None there, but the
/// Missing line still shows, so the flag must stay true). Synthetic names only.
/// </summary>
public sealed class SeriesInfoMissingFlagHttpTests : IClassFixture<SeriesInfoMissingFlagHttpTests.Host>
{
    private const string Shown = "mfshow";
    private const string Hidden = "mfhide";
    private readonly MangaPixerWebApplicationFactory _factory;

    public sealed class Host : IDisposable
    {
        public MangaPixerWebApplicationFactory Factory { get; } = MangaPixerWebApplicationFactory.WithoutBackgroundServices();

        public void Dispose() => Factory.Dispose();
    }

    public SeriesInfoMissingFlagHttpTests(Host host)
    {
        _factory = host.Factory;
    }

    private static readonly SemaphoreSlim SeedLock = new(1, 1);

    /// <summary>The same shapes in a library that shows series information (<paramref name="lib"/> = "mfshow") and one that hides it.</summary>
    private async Task SeedAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            if (await db.Libraries.AnyAsync(l => l.PublicId == Shown))
                return;
            foreach (var lib in new[] { Shown, Hidden })
                await SeedLibraryAsync(db, lib, hidden: lib == Hidden);
        }
        finally
        {
            SeedLock.Release();
        }
    }

    private static async Task SeedLibraryAsync(MangaPixerDbContext db, string pub, bool hidden)
    {
        var lib = new LibraryEntity
        {
            PublicId = pub,
            DisplayName = "Missing Flag " + pub,
            RootPath = "/synthetic/" + pub,
            CreatedAt = DateTimeOffset.UtcNow,
            MetadataSeriesInfoHidden = hidden,
        };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();

        async Task<CatalogNodeEntity> AddAsync(string id, long? parent, int kind, string name)
        {
            var node = new CatalogNodeEntity
            {
                PublicId = pub + id,
                LibraryId = lib.Id,
                ParentId = parent,
                Kind = kind,
                DisplayName = name,
                RelativePath = pub + id,
                PathKey = pub + id,
                SortKey = (kind == 0 ? "0" : "1") + name,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();
            if (kind == 1)
            {
                db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = node.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 2 });
                await db.SaveChangesAsync();
            }
            return node;
        }

        async Task<long> RecordAsync(string id)
        {
            var record = new MetadataRecordEntity
            {
                PublicId = pub + id + "rec",
                Provider = "mangaupdates",
                ExternalId = pub + id,
                Title = "Synthetic " + id + " Record",
                OriginVolumes = 5,
                FetchedAt = DateTimeOffset.UtcNow,
            };
            db.MetadataRecords.Add(record);
            await db.SaveChangesAsync();
            return record.Id;
        }

        async Task LinkAsync(CatalogNodeEntity node, SeriesLinkState state, long? recordId)
        {
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
            {
                NodeId = node.Id,
                LibraryId = lib.Id,
                State = (int)state,
                RecordId = recordId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Own Confirmed link, with an archive and a subfolder below it (both inherit the link: their anchor is this folder).
        var conf = await AddAsync("Conf", null, 0, "Synthetic Confirmed");
        await AddAsync("Confa0", conf.Id, 1, "Synthetic Confirmed v01");
        var child = await AddAsync("Child", conf.Id, 0, "Volume 2");
        await AddAsync("Childa0", child.Id, 1, "Synthetic Confirmed v02");
        await LinkAsync(conf, SeriesLinkState.Confirmed, await RecordAsync("Conf"));

        var auto = await AddAsync("Auto", null, 0, "Synthetic Auto");
        await AddAsync("Autoa0", auto.Id, 1, "Synthetic Auto v01");
        await LinkAsync(auto, SeriesLinkState.Auto, await RecordAsync("Auto"));

        // ComicInfo only: the series information shows (state ComicInfo), there is no report row.
        var ci = await AddAsync("Ci", null, 0, "Synthetic ComicInfo");
        var ciArchive = await AddAsync("Cia0", ci.Id, 1, "Synthetic ComicInfo v01");
        db.EmbeddedMetadata.Add(new EmbeddedMetadataEntity
        {
            NodeId = ciArchive.Id,
            Schema = 0,
            ContentVersion = 1,
            State = 1,
            Series = "Synthetic ComicInfo",
            Volume = 1,
        });
        await db.SaveChangesAsync();

        var dont = await AddAsync("Dont", null, 0, "Synthetic Dont Match");
        await AddAsync("Donta0", dont.Id, 1, "Synthetic Dont Match v01");
        await LinkAsync(dont, SeriesLinkState.DontMatch, null);

        var coll = await AddAsync("Coll", null, 0, "Synthetic Collection");
        await AddAsync("Colla0", coll.Id, 1, "Synthetic Collection Work");
        await LinkAsync(coll, SeriesLinkState.CollectionAbout, await RecordAsync("Coll"));

        var art = await AddAsync("Art", null, 0, "Synthetic Artist");
        await AddAsync("Arta0", art.Id, 1, "Synthetic Artist Work");
        await LinkAsync(art, SeriesLinkState.ArtistFolder, null);

        var review = await AddAsync("Review", null, 0, "Synthetic In Review");
        await AddAsync("Reviewa0", review.Id, 1, "Synthetic In Review v01");
        await LinkAsync(review, SeriesLinkState.NeedsReview, await RecordAsync("Review"));

        var none = await AddAsync("None", null, 0, "Synthetic Nothing");
        await AddAsync("Nonea0", none.Id, 1, "Synthetic Nothing v01");
    }

    private static readonly string[] AllNodes =
    [
        "Conf", "Confa0", "Child", "Childa0", "Auto", "Autoa0", "Ci", "Cia0", "Dont", "Donta0", "Coll", "Colla0", "Art", "Arta0",
        "Review", "Reviewa0", "None", "Nonea0",
    ];

    private async Task<HttpClient> AdminAsync()
    {
        await SeedAsync();
        return await _factory.LoginAsAdminWithChangedPasswordAsync();
    }

    private async Task<SeriesInfoDto> InfoAsync(HttpClient client, string nodeId)
    {
        var response = await client.GetAsync($"/api/v1/nodes/{nodeId}/series-info?includeItems=true");
        await _factory.UnhandledErrors.ExpectAsync(response, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SeriesInfoDto>(TestJson.Web))!;
    }

    private static async Task<HttpStatusCode> LineStatusAsync(HttpClient client, string nodeId) =>
        (await client.GetAsync($"/api/v1/nodes/{nodeId}/missing")).StatusCode;

    [Fact]
    public async Task Flag_IsTrue_ExactlyWhenTheMissingLineAnswers_ForEveryNode_ShownAndHidden()
    {
        var admin = await AdminAsync();
        foreach (var lib in new[] { Shown, Hidden })
        {
            foreach (var id in AllNodes)
            {
                var info = await InfoAsync(admin, lib + id);
                var line = await LineStatusAsync(admin, info.AnchorNodeId);
                Assert.True(line is HttpStatusCode.OK or HttpStatusCode.NotFound, $"{lib}{id}: {line}");
                Assert.True(info.MissingReportPossible == (line == HttpStatusCode.OK),
                    $"{lib}{id} (anchor {info.AnchorNodeId}, state {info.State}): flag {info.MissingReportPossible}, the line answered {(int)line}");
            }
        }
    }

    [Fact]
    public async Task ShownLibrary_FlagPerState()
    {
        var admin = await AdminAsync();

        // An own Confirmed / Auto link: the line exists (as 1.52.0's client rule said).
        var conf = await InfoAsync(admin, Shown + "Conf");
        Assert.Equal((SeriesInfoState.Web, Shown + "Conf", true), (conf.State, conf.AnchorNodeId, conf.MissingReportPossible));
        Assert.True((await InfoAsync(admin, Shown + "Auto")).MissingReportPossible);
        Assert.Equal(HttpStatusCode.OK, await LineStatusAsync(admin, Shown + "Conf"));
        Assert.Equal(HttpStatusCode.OK, await LineStatusAsync(admin, Shown + "Auto"));

        // Inherited: the subfolder and the archives are anchored to the linked folder, whose line exists.
        foreach (var id in new[] { "Child", "Confa0", "Childa0" })
        {
            var below = await InfoAsync(admin, Shown + id);
            Assert.Equal((Shown + "Conf", true), (below.AnchorNodeId, below.MissingReportPossible));
            Assert.Equal(HttpStatusCode.NotFound, await LineStatusAsync(admin, Shown + id)); // their own id has no row
        }

        // No row: ComicInfo only, Don't match, a collection, an artist folder, a link in review, nothing (state None - the 404s of 1.52.0).
        foreach (var (id, state) in new[]
        {
            ("Ci", SeriesInfoState.ComicInfo), ("Dont", SeriesInfoState.DontMatch), ("Coll", SeriesInfoState.CollectionAbout),
            ("Art", SeriesInfoState.ArtistFolder), ("Review", SeriesInfoState.None), ("None", SeriesInfoState.None),
        })
        {
            var info = await InfoAsync(admin, Shown + id);
            Assert.Equal((state, Shown + id, false), (info.State, info.AnchorNodeId, info.MissingReportPossible));
            Assert.Equal(HttpStatusCode.NotFound, await LineStatusAsync(admin, Shown + id));
        }

        // An archive anchored to itself (nothing above it applies): never a row.
        var loose = await InfoAsync(admin, Shown + "Nonea0");
        Assert.Equal((CatalogNodeKind.Archive, false), (loose.AnchorKind, loose.MissingReportPossible));
    }

    [Fact]
    public async Task HiddenLibrary_StateNone_KeepsTheLineOfALinkedFolder()
    {
        var admin = await AdminAsync();

        // "Show series information" off: every answer is the empty state None shape anchored to the node itself - but the linked folders'
        // Missing line still answers (it does not follow the switch), so their flag stays true: no line that shows today disappears.
        foreach (var id in new[] { "Conf", "Auto" })
        {
            var info = await InfoAsync(admin, Hidden + id);
            Assert.Equal((SeriesInfoState.None, Hidden + id, true), (info.State, info.AnchorNodeId, info.MissingReportPossible));
            Assert.Null(info.Link);
            Assert.Equal(HttpStatusCode.OK, await LineStatusAsync(admin, Hidden + id));
        }

        // Everything else: state None as well, and no request (the 404s the client could not tell apart in 1.52.0).
        foreach (var id in AllNodes.Except(["Conf", "Auto"]))
        {
            var info = await InfoAsync(admin, Hidden + id);
            Assert.Equal((SeriesInfoState.None, Hidden + id, false), (info.State, info.AnchorNodeId, info.MissingReportPossible));
            Assert.Equal(HttpStatusCode.NotFound, await LineStatusAsync(admin, Hidden + id));
        }
    }

    [Fact]
    public async Task Reader_SeesTheSameFlag_InIncognitoToo_AndWithoutAGrantNeither()
    {
        var admin = await AdminAsync();
        long shownId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            shownId = await db.Libraries.Where(l => l.PublicId == Shown).Select(l => l.Id).SingleAsync();
        }
        var reader = await AccessTestUsers.ReaderAsync(_factory, admin, "mfreader", [shownId], privateLibraryIds: [shownId]);
        foreach (var client in new[] { reader.Client, reader.Incognito })
        {
            Assert.True((await InfoAsync(client, Shown + "Conf")).MissingReportPossible);
            Assert.False((await InfoAsync(client, Shown + "None")).MissingReportPossible);
            Assert.Equal(HttpStatusCode.OK, await LineStatusAsync(client, Shown + "Conf"));
            // The hidden library is not granted: 404 for both, never a flag.
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/nodes/{Hidden}Conf/series-info")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, await LineStatusAsync(client, Hidden + "Conf"));
        }
    }
}
