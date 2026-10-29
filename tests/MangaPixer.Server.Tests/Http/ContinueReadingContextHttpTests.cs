using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Server.Http;

/// <summary>
/// HTTP tests (WebApplicationFactory) for the archive context line on Continue reading (1.50.0, lane W): <c>GET /api/v1/reading/continue</c>
/// (and the per-library list) carries <c>contextName</c> - the nearest ancestor folder that is not a Volumes / Chapters kind folder - for
/// an archive, null for an archive straight in a library root. Two users: a reader never gets a context name of a library the reader
/// cannot list (not granted, or Private while Incognito), and the answer holds a name only, never a path.
/// </summary>
public sealed class ContinueReadingContextHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string PubA = "ctxliba";
    private const string PubB = "ctxlibb";
    private readonly MangaPixerWebApplicationFactory _factory;

    public ContinueReadingContextHttpTests(MangaPixerWebApplicationFactory factory) => _factory = factory;

    // ctxliba: "Alpha Saga" > "Volumes" > "Vol. 012" ; "Alpha Saga" > "Ch. 0106.00 Vol. 006" ; "Loose Tale" (library root).
    // ctxlibb: "Hidden Saga" > "Vol. 001".
    private sealed record Seed(long LibA, long LibB, long Vol12, long Ch106, long Loose, long HiddenVol);

    private async Task<Seed> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var existingA = await db.Libraries.FirstOrDefaultAsync(l => l.PublicId == PubA);
        if (existingA is not null)
        {
            var ids = await db.CatalogNodes.Where(n => n.PublicId.StartsWith("ctxn")).ToDictionaryAsync(n => n.PublicId, n => n.Id);
            return new Seed(existingA.Id, (await db.Libraries.FirstAsync(l => l.PublicId == PubB)).Id,
                ids["ctxnVol12"], ids["ctxnCh106"], ids["ctxnLoose"], ids["ctxnHidden"]);
        }

        var now = DateTimeOffset.UtcNow;
        var libA = new LibraryEntity { PublicId = PubA, DisplayName = "Ctx A", RootPath = "/synthetic/ctxa", CreatedAt = now };
        var libB = new LibraryEntity { PublicId = PubB, DisplayName = "Ctx B", RootPath = "/synthetic/ctxb", CreatedAt = now };
        db.Libraries.AddRange(libA, libB);
        await db.SaveChangesAsync();

        var saga = Node("ctxnSaga", libA.Id, null, CatalogNodeKind.Folder, "Alpha Saga", now);
        var hiddenSaga = Node("ctxnHiddenSaga", libB.Id, null, CatalogNodeKind.Folder, "Hidden Saga", now);
        db.CatalogNodes.AddRange(saga, hiddenSaga);
        await db.SaveChangesAsync();
        var volumes = Node("ctxnVolumes", libA.Id, saga.Id, CatalogNodeKind.Folder, "Volumes", now);
        var ch106 = Node("ctxnCh106", libA.Id, saga.Id, CatalogNodeKind.Archive, "Ch. 0106.00 Vol. 006", now);
        var loose = Node("ctxnLoose", libA.Id, null, CatalogNodeKind.Archive, "Loose Tale", now);
        var hidden = Node("ctxnHidden", libB.Id, hiddenSaga.Id, CatalogNodeKind.Archive, "Vol. 001", now);
        db.CatalogNodes.AddRange(volumes, ch106, loose, hidden);
        await db.SaveChangesAsync();
        var vol12 = Node("ctxnVol12", libA.Id, volumes.Id, CatalogNodeKind.Archive, "Vol. 012", now);
        db.CatalogNodes.Add(vol12);
        await db.SaveChangesAsync();
        return new Seed(libA.Id, libB.Id, vol12.Id, ch106.Id, loose.Id, hidden.Id);
    }

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, CatalogNodeKind kind, string name, DateTimeOffset now) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = (int)kind,
        DisplayName = name,
        RelativePath = pub,
        PathKey = pub,
        SortKey = (kind == CatalogNodeKind.Folder ? "0" : "1") + name,
        CreatedAt = now,
    };

    private async Task ReadAsync(long userId, params long[] archiveIds)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        foreach (var id in archiveIds)
        {
            if (await db.ReadingProgress.AnyAsync(p => p.UserId == userId && p.ItemId == id))
                continue;
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = id, ContentVersion = 1, AnalysisState = 0, PageCount = 4 });
            db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = userId, ItemId = id, ContentVersion = 1, Ordinal = 1,
                State = (int)ReadingState.InProgress, Revision = 1, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<(List<ContinueReadingEntry> Entries, string Raw)> ContinueAsync(HttpClient client, string? library = null)
    {
        var url = library is null ? "/api/v1/reading/continue" : $"/api/v1/reading/continue/by-library/{library}";
        var raw = await client.GetStringAsync(url);
        var entries = System.Text.Json.JsonSerializer.Deserialize<List<ContinueReadingEntry>>(raw, TestJson.Web)!;
        return (entries.Where(e => e.LibraryId is PubA or PubB).ToList(), raw);
    }

    [Fact]
    public async Task Admin_GetsTheSeriesFolder_NotTheVolumesFolder_AndNullForARootArchive()
    {
        var seed = await SeedAsync();
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var adminId = await db.Users.Where(u => u.IsAdmin).Select(u => u.Id).FirstAsync();
            await ReadAsync(adminId, seed.Vol12, seed.Ch106, seed.Loose, seed.HiddenVol);
        }

        var (entries, raw) = await ContinueAsync(admin);
        var byId = entries.ToDictionary(e => e.ItemId);

        Assert.Equal("Alpha Saga", byId["ctxnVol12"].ContextName);   // Alpha Saga / Volumes / Vol. 012
        Assert.Equal("Alpha Saga", byId["ctxnCh106"].ContextName);   // Alpha Saga / Ch. 0106.00 Vol. 006
        Assert.Null(byId["ctxnLoose"].ContextName);                  // straight in the library root
        Assert.Equal("Hidden Saga", byId["ctxnHidden"].ContextName); // admin sees every library
        Assert.DoesNotContain("/synthetic", raw, StringComparison.Ordinal); // a name, never a path

        var (perLibrary, _) = await ContinueAsync(admin, PubA);
        Assert.Equal("Alpha Saga", perLibrary.Single(e => e.ItemId == "ctxnVol12").ContextName);
        Assert.DoesNotContain(perLibrary, e => e.ItemId == "ctxnHidden");
    }

    [Fact]
    public async Task Reader_NeverGetsTheContextOfALibraryTheyCannotList()
    {
        var seed = await SeedAsync();
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        // Granted library A only; library B is Private-free but not granted. The reader also has a stale progress row in B.
        var reader = await AccessTestUsers.ReaderAsync(_factory, admin, "ctxreader", [seed.LibA]);
        await ReadAsync(reader.UserId, seed.Vol12, seed.Loose, seed.HiddenVol);

        var (entries, raw) = await ContinueAsync(reader.Client);

        var byId = entries.ToDictionary(e => e.ItemId);
        Assert.Equal("Alpha Saga", byId["ctxnVol12"].ContextName);
        Assert.Null(byId["ctxnLoose"].ContextName);
        Assert.DoesNotContain("ctxnHidden", byId.Keys);
        Assert.DoesNotContain("Hidden Saga", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Incognito_DropsAPrivateLibrarysEntriesAndTheirContext()
    {
        var seed = await SeedAsync();
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var reader = await AccessTestUsers.ReaderAsync(_factory, admin, "ctxprivate", [seed.LibA, seed.LibB], privateLibraryIds: [seed.LibB]);
        await ReadAsync(reader.UserId, seed.Vol12, seed.HiddenVol);

        var (normal, _) = await ContinueAsync(reader.Client);
        var (incognito, incognitoRaw) = await ContinueAsync(reader.Incognito);

        Assert.Equal("Hidden Saga", normal.Single(e => e.ItemId == "ctxnHidden").ContextName);
        Assert.Equal("Alpha Saga", incognito.Single(e => e.ItemId == "ctxnVol12").ContextName);
        Assert.DoesNotContain("ctxnHidden", incognito.Select(e => e.ItemId));
        Assert.DoesNotContain("Hidden Saga", incognitoRaw, StringComparison.Ordinal);
    }
}
