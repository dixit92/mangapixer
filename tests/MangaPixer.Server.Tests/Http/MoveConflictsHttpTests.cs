namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Library.Moves;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) of cross-library moves (1.31.0): the move-conflicts admin API (admin-only, list /
/// count / resolve), the DI wiring (holds, pairing runner, no relink service), and a move recognised after the fact seen
/// through the public progress endpoint - including a reader who cannot see the destination library until granted.
/// </summary>
[Trait("Category", "Http")]
[Collection("HttpSerial")]
public sealed class MoveConflictsHttpTests : IClassFixture<MoveConflictsHttpTests.Host>
{
    /// <summary>One host for the class; it must never contact a provider (moves are local).</summary>
    public sealed class Host : IDisposable
    {
        public MetadataNetworkWebApplicationFactory Factory { get; } = new(failOnAnyRequest: true);

        public void Dispose() => Factory.Dispose();
    }

    private readonly MetadataNetworkWebApplicationFactory _factory;

    public MoveConflictsHttpTests(Host host) => _factory = host.Factory;

    private static CatalogNodeEntity Archive(string pub, long libId, int availability, DateTimeOffset created) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        Kind = 1,
        DisplayName = pub,
        RelativePath = pub + ".cbz",
        PathKey = pub + ".cbz",
        SortKey = "1" + pub,
        Availability = availability,
        CreatedAt = created,
        TombstonedAt = availability == 5 ? created.AddMinutes(1) : null,
        ArchiveItem = new ArchiveItemEntity { ContentVersion = 1, AnalysisState = 0, PageCount = 3, ByteLength = 4096 },
    };

    private static void Pages(MangaPixerDbContext db, long itemId)
    {
        for (var i = 0; i < 3; i++)
            db.PageEntries.Add(new PageEntryEntity
            {
                ItemId = itemId,
                ContentVersion = 1,
                Ordinal = i,
                EntryKey = new PageEntryKey(i).ToOpaque(),
                SourceEntryLocator = $"p{i}.jpg",
                MediaType = "image/jpeg",
                ByteSize = 100 + i,
            });
    }

    /// <summary>
    /// Two libraries; old copy <paramref name="prefix"/>Old (in "from", still LIVE: the host's own startup pass must not pair
    /// it before the test has added its rows - <see cref="TombstoneAsync"/> removes it) and new copy <paramref name="prefix"/>New
    /// (live in "to", created after the old one was last seen), same signature and pages. Returns (old id, new id).
    /// </summary>
    private async Task<(long Old, long New, string FromLib, string ToLib)> SeedPairAsync(string prefix, string signature)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var from = new LibraryEntity { PublicId = prefix + "from", DisplayName = "From " + prefix, RootPath = "/synthetic/" + prefix + "a", CreatedAt = DateTimeOffset.UtcNow };
        var to = new LibraryEntity { PublicId = prefix + "to", DisplayName = "To " + prefix, RootPath = "/synthetic/" + prefix + "b", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.AddRange(from, to);
        await db.SaveChangesAsync();
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        var old = Archive(prefix + "Old", from.Id, 0, t0);
        var neu = Archive(prefix + "New", to.Id, 0, t0.AddHours(1));
        old.ArchiveItem!.ContentSignature = signature;
        neu.ArchiveItem!.ContentSignature = signature;
        db.CatalogNodes.AddRange(old, neu);
        await db.SaveChangesAsync();
        Pages(db, old.Id);
        Pages(db, neu.Id);
        await db.SaveChangesAsync();
        return (old.Id, neu.Id, from.PublicId, to.PublicId);
    }

    /// <summary>What the source scan does when the old copy is gone: tombstone it (after the old copy was last seen).</summary>
    private async Task TombstoneAsync(long nodeId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var at = DateTimeOffset.UtcNow.AddHours(-1).AddMinutes(-30);
        await db.CatalogNodes.Where(n => n.Id == nodeId)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.Availability, 5).SetProperty(n => n.TombstonedAt, at));
    }

    private static string Signature(char c) => "v1:4096:" + new string(c, 64);

    private async Task RunPairingAsync()
    {
        var runner = _factory.Services.GetRequiredService<MovePairingRunner>();
        runner.Debounce = TimeSpan.Zero;
        await runner.RequestRun();
    }

    [Fact]
    public async Task Endpoints_AreAdminOnly()
    {
        using var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/admin/move-conflicts")).StatusCode);
        var reader = await _factory.CreateReaderClientAsync("mcreader", null);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/admin/move-conflicts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/admin/move-conflicts/count")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/admin/move-conflicts/resolve",
            new MoveConflictResolveRequest { All = true, Resolution = MoveConflictResolution.Keep })).StatusCode);
    }

    [Fact]
    public async Task Wiring_HoldsRunnerAndNoRelinkService()
    {
        using var scope = _factory.Services.CreateScope();
        Assert.IsType<MoveTombstoneHolds>(scope.ServiceProvider.GetRequiredService<ITombstoneHolds>());
        Assert.Same(scope.ServiceProvider.GetRequiredService<MovePairingRunner>(), scope.ServiceProvider.GetRequiredService<IMovePairingTrigger>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<MovePairingService>());
        Assert.DoesNotContain(typeof(MovePairingService).Assembly.GetTypes(), t => t.Name == "IdentityRelinkService");
    }

    [Fact]
    public async Task APairedMove_CopiesProgress_VisibleOnceTheReaderMayOpenTheDestination()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var (oldId, newId, fromLib, toLib) = await SeedPairAsync("mcv", Signature('a'));
        var reader = await _factory.CreateReaderClientAsync("mcvisitor", fromLib); // may read the source only
        long readerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            readerId = await db.Users.Where(u => u.NormalizedUserName == "MCVISITOR").Select(u => u.Id).SingleAsync();
            db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = readerId,
                ItemId = oldId,
                ContentVersion = 1,
                Ordinal = 2,
                EntryKey = new PageEntryKey(2).ToOpaque(),
                State = 1,
                Revision = 1,
                LastMutationId = "m1",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await TombstoneAsync(oldId);
        await RunPairingAsync();

        Assert.NotEqual(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/reading/progress/mcvNew")).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            Assert.True(await db.NodeMoves.AnyAsync(m => m.FromNodeId == oldId && m.ToNodeId == newId));
            var libId = await db.Libraries.Where(l => l.PublicId == toLib).Select(l => l.Id).SingleAsync();
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = readerId, LibraryId = libId, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var response = await reader.GetAsync("/api/v1/reading/progress/mcvNew");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var progress = await response.Content.ReadFromJsonAsync<ReadingProgressDto>(TestJson.Web);
        Assert.Equal(2, progress!.PageIndex);
        Assert.NotNull(admin);
    }

    [Fact]
    public async Task Conflicts_ListCountAndResolve()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var (oldId, newId, _, _) = await SeedPairAsync("mcc", Signature('b'));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var adminId = await db.Users.Where(u => u.NormalizedUserName == "ADMIN").Select(u => u.Id).SingleAsync();
            foreach (var (item, ordinal) in new[] { (oldId, 2), (newId, 0) })
                db.ReadingProgress.Add(new ReadingProgressEntity
                {
                    UserId = adminId,
                    ItemId = item,
                    ContentVersion = 1,
                    Ordinal = ordinal,
                    EntryKey = new PageEntryKey(ordinal).ToOpaque(),
                    State = 1,
                    Revision = 1,
                    LastMutationId = "c" + item,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            await db.SaveChangesAsync();
        }

        await TombstoneAsync(oldId);
        await RunPairingAsync();

        var count = await admin.GetFromJsonAsync<MoveConflictCountDto>("/api/v1/admin/move-conflicts/count", TestJson.Web);
        Assert.True(count!.Open >= 1);
        var page = await admin.GetFromJsonAsync<MoveConflictPageDto>("/api/v1/admin/move-conflicts", TestJson.Web);
        var conflict = Assert.Single(page!.Items, i => i.NodeId == "mccNew");
        Assert.Equal(MoveConflictKind.Progress, conflict.Kind);
        Assert.Equal(3, conflict.Old.Page);
        Assert.Equal(1, conflict.New.Page);
        Assert.Equal("From mcc", conflict.FromLibraryName);
        Assert.Equal("admin", conflict.UserName);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/move-conflicts?state=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/admin/move-conflicts/resolve",
            new MoveConflictResolveRequest { Resolution = MoveConflictResolution.Keep })).StatusCode);

        var resolve = await admin.PostAsJsonAsync("/api/v1/admin/move-conflicts/resolve",
            new MoveConflictResolveRequest { Ids = [conflict.Id, "zzzz"], Resolution = MoveConflictResolution.Overwrite });
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        var result = await resolve.Content.ReadFromJsonAsync<MoveConflictResolveResultDto>(TestJson.Web);
        Assert.Equal((1, 1), (result!.Resolved, result.Skipped));

        var progress = await admin.GetFromJsonAsync<ReadingProgressDto>("/api/v1/reading/progress/mccNew", TestJson.Web);
        Assert.Equal(2, progress!.PageIndex);
        var resolved = await admin.GetFromJsonAsync<MoveConflictPageDto>("/api/v1/admin/move-conflicts?state=resolved", TestJson.Web);
        Assert.Contains(resolved!.Items, i => i.Id == conflict.Id && i.State == MoveConflictState.Overwritten);
    }
}
