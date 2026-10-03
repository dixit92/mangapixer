namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) of the read-only metadata export (1.33.0): the export policy (admin cookie 200, reader 403,
/// anonymous 401), paging, <c>updatedSince</c> (inclusive) with removals, 409 <c>fullSyncRequired</c>, <c>include</c>, and that no
/// response carries a path-like field or a path value. Synthetic names only.
/// </summary>
[Trait("Category", "Http")]
public sealed class ExportHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "exlib1";
    private const string RootPath = "/synthetic/export-root-x9";
    private readonly MangaPixerWebApplicationFactory _factory;

    public ExportHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return;
        var lib = new LibraryEntity { PublicId = LibPubId, DisplayName = "Export Http", RootPath = RootPath, CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        db.DeclaredFacts.Add(new DeclaredFactEntity
        {
            LibraryId = lib.Id,
            Key = DeclaredFactKeys.Type,
            Value = "manga",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var shelf = Node("exShelf", lib.Id, null, 0, "Shonen");
        db.CatalogNodes.Add(shelf);
        await db.SaveChangesAsync();
        for (var i = 1; i <= 3; i++)
        {
            var folder = Node($"exSeries{i}", lib.Id, shelf.Id, 0, $"Export Series {i}");
            db.CatalogNodes.Add(folder);
            await db.SaveChangesAsync();
            var archive = Node($"exSeries{i}v1", lib.Id, folder.Id, 1, $"Export Series {i} v01.cbz");
            db.CatalogNodes.Add(archive);
            var record = new MetadataRecordEntity
            {
                PublicId = $"exrec{i}",
                Provider = "mangaupdates",
                ExternalId = $"80{i}",
                Title = $"Export Series {i}",
                OriginStatus = (int)MetadataOriginStatus.Ongoing,
                FetchedAt = DateTimeOffset.UtcNow,
            };
            db.MetadataRecords.Add(record);
            await db.SaveChangesAsync();
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
            {
                NodeId = folder.Id,
                LibraryId = lib.Id,
                State = (int)SeriesLinkState.Confirmed,
                RecordId = record.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, int kind, string name) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = kind,
        DisplayName = name,
        RelativePath = "rel/" + pub,
        PathKey = "rel/" + pub,
        SortKey = (kind == 0 ? "0" : "1") + name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<HttpClient> AdminAsync()
    {
        await SeedAsync();
        return await _factory.LoginAsAdminWithChangedPasswordAsync();
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Libraries_ListsKindAndCounts()
    {
        var admin = await AdminAsync();
        var root = await OkAsync(await admin.GetAsync("/api/v1/export/libraries"));
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var lib = root.GetProperty("libraries").EnumerateArray().Single(l => l.GetProperty("id").GetString() == LibPubId);
        Assert.Equal(("Export Http", "manga", 4, 3), (lib.GetProperty("displayName").GetString(), lib.GetProperty("kind").GetString(),
            lib.GetProperty("folderCount").GetInt32(), lib.GetProperty("itemCount").GetInt32()));
        Assert.Equal(JsonValueKind.Null, lib.GetProperty("lastScanAt").ValueKind);
    }

    [Fact]
    public async Task Metadata_PagesEveryItem_AndUpdatedSinceIsInclusive()
    {
        var admin = await AdminAsync();
        var first = await OkAsync(await admin.GetAsync($"/api/v1/export/metadata?library={LibPubId}&limit=2"));
        Assert.Equal(1, first.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(("manga", LibPubId), (first.GetProperty("library").GetProperty("kind").GetString(), first.GetProperty("library").GetProperty("id").GetString()));
        var serverTime = first.GetProperty("serverTime").GetString()!;
        Assert.EndsWith("Z", serverTime, StringComparison.Ordinal);
        var ids = first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("nodeId").GetString()!).ToList();
        Assert.Equal(2, ids.Count);
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        var second = await OkAsync(await admin.GetAsync($"/api/v1/export/metadata?library={LibPubId}&limit=2&cursor={cursor}"));
        ids.AddRange(second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("nodeId").GetString()!));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal(["exSeries1", "exSeries2", "exSeries3"], ids.Order());

        var item = first.GetProperty("items")[0];
        Assert.Equal(["Shonen", item.GetProperty("trail")[1].GetString()], item.GetProperty("trail").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("folder", item.GetProperty("nodeKind").GetString());
        Assert.Equal("Confirmed", item.GetProperty("link").GetProperty("state").GetString());
        Assert.Equal("mangaupdates", item.GetProperty("record").GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Array, item.GetProperty("officialLinks").ValueKind);

        // The first page's serverTime as updatedSince: inclusive, so the items of that rebuild come again (repeats are allowed).
        var since = await OkAsync(await admin.GetAsync($"/api/v1/export/metadata?library={LibPubId}&updatedSince={Uri.EscapeDataString(serverTime)}"));
        Assert.True(since.GetProperty("items").GetArrayLength() >= 1);
        Assert.Equal(JsonValueKind.Array, since.GetProperty("removed").ValueKind);
    }

    [Fact]
    public async Task Metadata_BeforeThePool_Is409_AndErrorsAreCodes()
    {
        var admin = await AdminAsync();
        await OkAsync(await admin.GetAsync($"/api/v1/export/metadata?library={LibPubId}"));

        var old = await admin.GetAsync($"/api/v1/export/metadata?library={LibPubId}&updatedSince=2001-01-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.Conflict, old.StatusCode);
        Assert.Equal("{\"error\":\"fullSyncRequired\"}", await old.Content.ReadAsStringAsync());

        var missing = await admin.GetAsync("/api/v1/export/metadata?library=nolib");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("libraryNotFound", (await missing.Content.ReadFromJsonAsync<ExportErrorDto>(TestJson.Web))!.Error);
        var noLibrary = await admin.GetAsync("/api/v1/export/metadata");
        Assert.Equal(HttpStatusCode.BadRequest, noLibrary.StatusCode);
        var badCursor = await admin.GetAsync($"/api/v1/export/metadata?library={LibPubId}&cursor=%40%40");
        Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);
    }

    [Fact]
    public async Task Metadata_IncludeDropsTheOptionalBlocks()
    {
        var admin = await AdminAsync();
        var page = await OkAsync(await admin.GetAsync($"/api/v1/export/metadata?library={LibPubId}&include=completion"));
        var item = page.GetProperty("items")[0];
        Assert.True(item.TryGetProperty("completion", out _));
        Assert.False(item.TryGetProperty("volumes", out _));
        Assert.False(item.TryGetProperty("refresh", out _));
        var all = (await OkAsync(await admin.GetAsync($"/api/v1/export/metadata?library={LibPubId}"))).GetProperty("items")[0];
        Assert.True(all.TryGetProperty("volumes", out _) && all.TryGetProperty("refresh", out _) && all.TryGetProperty("completion", out _));
    }

    [Fact]
    public async Task Export_NeedsTheExportPolicy_ReaderIs403_AnonymousIs401()
    {
        var admin = await AdminAsync();
        var create = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = "exreader", Password = "ReaderPass123!", IsAdmin = false });
        create.EnsureSuccessStatusCode();
        var first = _factory.CreateClient();
        (await first.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "exreader", Password = "ReaderPass123!" })).EnsureSuccessStatusCode();
        var csrf = await (await first.GetAsync("/api/v1/auth/csrf")).Content.ReadFromJsonAsync<CsrfTokenDto>();
        first.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
        (await first.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "ReaderPass123!",
            NewPassword = "ReaderNewPass123!",
        })).EnsureSuccessStatusCode();
        var reader = _factory.CreateClient();
        (await reader.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "exreader", Password = "ReaderNewPass123!" })).EnsureSuccessStatusCode();

        foreach (var url in new[] { "/api/v1/export/libraries", $"/api/v1/export/metadata?library={LibPubId}" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task Export_CarriesNoPathLikeFieldAndNoPathValue()
    {
        var admin = await AdminAsync();
        var bodies = new[]
        {
            await (await admin.GetAsync("/api/v1/export/libraries")).Content.ReadAsStringAsync(),
            await (await admin.GetAsync($"/api/v1/export/metadata?library={LibPubId}")).Content.ReadAsStringAsync(),
        };
        string[] forbidden = ["path", "filepath", "sourcepath", "absolutepath", "fullpath", "realpath", "diskpath", "mediapath", "rootpath",
            "librarypath", "relativepath", "pathkey"];
        foreach (var body in bodies)
        {
            Assert.DoesNotContain(RootPath, body, StringComparison.Ordinal);
            Assert.DoesNotContain("rel/", body, StringComparison.Ordinal);
            Walk(JsonDocument.Parse(body).RootElement);
        }

        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                {
                    Assert.DoesNotContain(p.Name.ToLowerInvariant(), forbidden);
                    Walk(p.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var i in e.EnumerateArray())
                    Walk(i);
            }
        }
    }
}
