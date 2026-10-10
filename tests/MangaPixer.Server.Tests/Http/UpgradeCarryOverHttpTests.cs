namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Library.Moves;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) of read state that follows a chapter-to-volume upgrade (1.40.0): the step runs inside the move-pairing
/// runner resolved from DI (the wiring), and after a pass the new volume reports READ (or in progress at page 1) through the public reading
/// and browse API - the read mark, the single-node lookup, the folder list and Continue reading. Never contacts a provider (the host fails on
/// any request).
/// </summary>
[Trait("Category", "Http")]
public sealed class UpgradeCarryOverHttpTests : IClassFixture<UpgradeCarryOverHttpTests.Host>
{
    public sealed class Host : IDisposable
    {
        public MetadataNetworkWebApplicationFactory Factory { get; } = new(failOnAnyRequest: true);

        public void Dispose() => Factory.Dispose();
    }

    private const int Pages = 6;

    private readonly MetadataNetworkWebApplicationFactory _factory;

    public UpgradeCarryOverHttpTests(Host host) => _factory = host.Factory;

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, int kind, string name, DateTimeOffset created) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = kind,
        DisplayName = name,
        RelativePath = "Synthetic Saga/" + name,
        PathKey = "synthetic saga/" + name.ToLowerInvariant(),
        SortKey = "1" + name.ToLowerInvariant(),
        Availability = 0,
        CreatedAt = created,
        ArchiveItem = kind == 1 ? new ArchiveItemEntity { ContentVersion = 1, AnalysisState = 0, PageCount = Pages, ByteLength = 4096 } : null,
    };

    /// <summary>
    /// A linked folder (Confirmed, with a stored list: volume 1 = chapters 1-3 + 2.5) holding chapters 1-3 and 2.5, read by the given readers;
    /// then the upgrade: the chapters tombstoned, the analysed volume 1 file created after them. Returns (library, folder, volume) public ids.
    /// </summary>
    private async Task<(string Library, string Folder, string Volume)> SeedAsync(string prefix, Dictionary<long, string[]> readByUser)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var library = new LibraryEntity { PublicId = prefix + "lib", DisplayName = "Upgrade " + prefix, RootPath = "/synthetic/" + prefix, CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        var folder = Node(prefix + "fold", library.Id, null, 0, "Synthetic Saga", t0);
        db.CatalogNodes.Add(folder);
        await db.SaveChangesAsync();
        var chapters = new Dictionary<string, CatalogNodeEntity>(StringComparer.Ordinal);
        foreach (var number in new[] { "001", "002", "002.5", "003" })
            chapters[number] = Node(prefix + "c" + number.Replace(".", "x", StringComparison.Ordinal), library.Id, folder.Id, 1, $"Synthetic Saga c{number}.cbz", t0);
        db.CatalogNodes.AddRange(chapters.Values);
        var record = new MetadataRecordEntity { PublicId = prefix + "rec", Provider = "mangaupdates", ExternalId = prefix + "500", Title = "Synthetic Saga", FetchedAt = DateTimeOffset.UtcNow };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = folder.Id,
            LibraryId = library.Id,
            State = (int)SeriesLinkState.Confirmed,
            RecordId = record.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
        {
            RecordId = record.Id,
            Source = (int)VolumeMapSource.MangaDexAggregate,
            State = (int)VolumeMapState.Ok,
            VolumesJson = """[{"v":"1","c":["1","2","2.5","3"]},{"v":"2","c":["4","5","6"]}]""",
            ContentHash = "h1",
            Version = 1,
            FetchedAt = DateTimeOffset.UtcNow,
        });
        foreach (var (userId, numbers) in readByUser)
        {
            foreach (var number in numbers)
                db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = chapters[number].Id, MarkedAt = t0.AddMinutes(10), Source = "manual" });
        }
        await db.SaveChangesAsync();

        // The upgrade: the chapters are gone (tombstoned), then the volume file appears and is analysed.
        var gone = chapters.Values.Select(c => c.Id).ToList();
        await db.CatalogNodes.Where(n => gone.Contains(n.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.Availability, 5).SetProperty(n => n.TombstonedAt, t0.AddHours(1)));
        var volume = Node(prefix + "vol", library.Id, folder.Id, 1, "Synthetic Saga v01 (2026) (Digital).cbz", t0.AddHours(2));
        db.CatalogNodes.Add(volume);
        await db.SaveChangesAsync();
        for (var i = 0; i < Pages; i++)
            db.PageEntries.Add(new PageEntryEntity
            {
                ItemId = volume.Id,
                ContentVersion = 1,
                Ordinal = i,
                EntryKey = new PageEntryKey(i).ToOpaque(),
                SourceEntryLocator = $"p{i}.jpg",
                MediaType = "image/jpeg",
                ByteSize = 100 + i,
            });
        await db.SaveChangesAsync();
        return (library.PublicId, folder.PublicId, volume.PublicId);
    }

    private async Task<long> UserIdAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await db.Users.Where(u => u.NormalizedUserName == name.ToUpperInvariant()).Select(u => u.Id).SingleAsync();
    }

    private async Task RunPairingAsync()
    {
        var runner = _factory.Services.GetRequiredService<MovePairingRunner>();
        runner.Debounce = TimeSpan.Zero;
        await runner.RequestRun();
    }

    [Fact]
    public async Task AfterThePass_TheVolumeReportsReadOrInProgressThroughTheReadingAndBrowseApi()
    {
        await _factory.LoginAsAdminWithChangedPasswordAsync();
        const string prefix = "upg";
        // Readers are created before the seed so their ids exist; they are granted the library afterwards.
        var finisher = await _factory.CreateReaderClientAsync("upgfinisher", null);
        var sampler = await _factory.CreateReaderClientAsync("upgsampler", null);
        var finisherId = await UserIdAsync("upgfinisher");
        var samplerId = await UserIdAsync("upgsampler");
        var (library, folder, volume) = await SeedAsync(prefix, new Dictionary<long, string[]>
        {
            [finisherId] = ["001", "002", "003"], // the extra (2.5) is optional
            [samplerId] = ["002"],
        });
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var libId = await db.Libraries.Where(l => l.PublicId == library).Select(l => l.Id).SingleAsync();
            foreach (var userId in new[] { finisherId, samplerId })
                db.LibraryGrants.Add(new LibraryGrantEntity { UserId = userId, LibraryId = libId, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await RunPairingAsync();

        // Read: the read mark, the node, the folder list, and the progress (opens at page 1, not in Continue reading).
        Assert.True((await finisher.GetFromJsonAsync<ReadMarkDto>($"/api/v1/reading/{volume}/read", TestJson.Web))!.IsRead);
        Assert.True((await finisher.GetFromJsonAsync<CatalogNodeDto>($"/api/v1/nodes/{volume}", TestJson.Web))!.IsRead);
        var list = await finisher.GetFromJsonAsync<PageResponse<CatalogNodeDto>>(
            $"/api/v1/libraries/{library}/browse?parentId={folder}&group=flat", TestJson.Web);
        Assert.True(Assert.Single(list!.Items, n => n.Id == volume).IsRead);
        var done = await finisher.GetFromJsonAsync<ReadingProgressDto>($"/api/v1/reading/progress/{volume}", TestJson.Web);
        Assert.Equal(ReadingState.Completed, done!.State);
        Assert.Equal(0, done.OpenPageIndex);
        Assert.DoesNotContain((await finisher.GetFromJsonAsync<List<ContinueReadingEntry>>("/api/v1/reading/continue", TestJson.Web))!, e => e.ItemId == volume);

        // In progress at page 1: not read, resumes at the first page, listed in Continue reading.
        Assert.False((await sampler.GetFromJsonAsync<ReadMarkDto>($"/api/v1/reading/{volume}/read", TestJson.Web))!.IsRead);
        var started = await sampler.GetFromJsonAsync<ReadingProgressDto>($"/api/v1/reading/progress/{volume}", TestJson.Web);
        Assert.Equal(ReadingState.InProgress, started!.State);
        Assert.Equal(0, started.PageIndex);
        Assert.Contains((await sampler.GetFromJsonAsync<List<ContinueReadingEntry>>("/api/v1/reading/continue", TestJson.Web))!, e => e.ItemId == volume);

        // A second pass changes nothing.
        await RunPairingAsync();
        var again = await sampler.GetFromJsonAsync<ReadingProgressDto>($"/api/v1/reading/progress/{volume}", TestJson.Web);
        Assert.Equal(started.Revision, again!.Revision);
    }
}
