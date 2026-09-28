namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Tests.Server.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) of the cover comparison wiring (1.28.0): the PRODUCTION matcher core and
/// <see cref="AutoMatchCoverComparer"/> from DI, reached by "Match now" and a real worker pass; only the hash is
/// faked (the process tests cover the worker's <c>image_hash</c>). A title tie in a volume folder downloads the two
/// candidates' covers, the matching one ranks first in Needs review, and no folder name, title or image address
/// reaches a log line. <c>Metadata:AutoMatch:CompareCovers=false</c> downloads nothing.
/// </summary>
[Trait("Category", "Http")]
[Collection("HttpSerial")]
public sealed class MetadataCoverCompareHttpTests
{
    private const string LibPub = "cclib1";
    private const string Sentinel = "Qzvcoversentinel";
    private static readonly byte[] s_png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>"A" files hash to 0, anything else to all ones.</summary>
    private sealed class MarkerHasher : ICoverHasher
    {
        public async Task<ulong?> HashFileAsync(string path, CancellationToken ct) =>
            (await File.ReadAllBytesAsync(path, ct))[^1] == (byte)'A' ? 0UL : ulong.MaxValue;
    }

    private static void Services(IServiceCollection services, bool compareCovers)
    {
        services.AddSingleton<ICoverHasher, MarkerHasher>();
        services.AddSingleton(new MetadataRateLimitOptions { AutomaticInterval = TimeSpan.Zero });
        services.AddSingleton(new MetadataAutoMatchOptions { WorkerEnabled = false, CompareCovers = compareCovers });
    }

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

    private static string ImageUrl(long id) => $"https://{MetadataHttp.MangaUpdatesImageHost}/image/thumb/{Sentinel}{id}.png";

    private static async Task<(HttpClient Admin, int Images)> RunAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Cover Lib", RootPath = "/synthetic/cc", CreatedAt = DateTimeOffset.UtcNow, MetadataEnabled = true };
            db.Libraries.Add(lib);
            await db.SaveChangesAsync();
            var folder = Node("ccFolder", lib.Id, null, CatalogNodeKind.Folder, $"{Sentinel} Saga");
            db.CatalogNodes.Add(folder);
            await db.SaveChangesAsync();
            var archives = Enumerable.Range(1, 3).Select(i => Node($"ccA{i}", lib.Id, folder.Id, CatalogNodeKind.Archive, $"{Sentinel} Saga v0{i}")).ToList();
            db.CatalogNodes.AddRange(archives);
            await db.SaveChangesAsync();
            db.ArchiveItems.AddRange(archives.Select(a => new ArchiveItemEntity { NodeId = a.Id, ContentVersion = 1, PageCount = 2 }));
            await db.SaveChangesAsync();
            // The stored thumbnail of the folder's cover archive (v01): what the comparison hashes locally.
            var thumbnail = factory.Services.GetRequiredService<ThumbnailStore>().GetThumbnailPath(archives[0].Id, 1);
            Directory.CreateDirectory(Path.GetDirectoryName(thumbnail)!);
            await File.WriteAllBytesAsync(thumbnail, [.. s_png, (byte)'A']);
        }

        factory.Handler.Respond = request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == MetadataHttp.MangaUpdatesImageHost)
                return ScriptedHandler.Bytes([.. s_png, (byte)(uri.AbsolutePath.EndsWith("902.png", StringComparison.Ordinal) ? 'A' : 'B')]);
            if (request.Method == HttpMethod.Post)
                return ScriptedHandler.Json(MuJson.Search(new MuJson.Hit(901, $"{Sentinel} Saga", Image: ImageUrl(901)),
                    new MuJson.Hit(902, $"{Sentinel} Saga", Image: ImageUrl(902))));
            var id = long.Parse(uri.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
            return ScriptedHandler.Json(MuJson.Get(id, $"{Sentinel} Saga", image: ImageUrl(id)));
        };
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest
        {
            FetchEnabled = true,
            AcceptedConsentVersion = MetadataConsent.CurrentVersion,
            AutoMatchEnabled = true,
            AcceptedAutoConsentVersion = MetadataAutoConsent.CurrentVersion,
        })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}/match", new MetadataMatchLibraryRequest())).EnsureSuccessStatusCode();

        var worker = factory.Services.GetServices<IHostedService>().OfType<MetadataAutoMatchHostedService>().Single();
        Assert.Equal(1, await worker.RunPassAsync(CancellationToken.None));
        return (admin, factory.Handler.Seen.Count(r => r.Uri.Host == MetadataHttp.MangaUpdatesImageHost));
    }

    private static async Task<MetadataReviewItemDto> ReviewRowAsync(HttpClient admin)
    {
        var response = await admin.GetAsync("/api/v1/admin/metadata/review?tab=NeedsReview");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var page = (await response.Content.ReadFromJsonAsync<MetadataReviewPageDto>(TestJson.Web))!;
        return Assert.Single(page.Items, i => i.NodeId == "ccFolder");
    }

    [Fact]
    public async Task WorkerPass_ATitleTie_IsBrokenByTheCover_AndNoNameOrImageAddressReachesALogLine()
    {
        var sink = new CollectingSink();
        using var factory = new MetadataNetworkWebApplicationFactory(sink: sink, configureServices: s => Services(s, compareCovers: true));

        var (admin, images) = await RunAsync(factory);

        Assert.Equal(2, images);
        var row = await ReviewRowAsync(admin);
        Assert.Equal(["902", "901"], row.Candidates.Select(c => c.ExternalId).Take(2)); // without the cover, 901 ranks first
        Assert.Contains("close_second", row.Reasons);
        Assert.Contains(sink.Events, e => e.MessageTemplate.Text.Contains("covers compared", StringComparison.Ordinal));
        foreach (var e in sink.Events)
        {
            var rendered = e.RenderMessage() + " " + string.Join(" ", e.Properties.Select(p => p.Value.ToString())) + " " + e.Exception;
            Assert.False(rendered.Contains(Sentinel, StringComparison.OrdinalIgnoreCase), "A name or image address leaked into a log line: " + e.MessageTemplate.Text);
            Assert.False(rendered.Contains("image/thumb", StringComparison.OrdinalIgnoreCase), "An image address leaked into a log line: " + e.MessageTemplate.Text);
        }
    }

    [Fact]
    public async Task WorkerPass_WithCompareCoversOff_DownloadsNoCover()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: s => Services(s, compareCovers: false));

        var (admin, images) = await RunAsync(factory);

        Assert.Equal(0, images);
        Assert.Equal("901", (await ReviewRowAsync(admin)).Candidates[0].ExternalId);
    }

    [Fact]
    public void Wiring_TheComparerAndItsParts_Resolve()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        Assert.NotNull(sp.GetRequiredService<AutoMatchCoverComparer>());
        Assert.IsType<WorkerCoverHasher>(sp.GetRequiredService<ICoverHasher>());
        Assert.IsType<DefaultCoverCompareSetting>(sp.GetRequiredService<ICoverCompareSetting>());
        Assert.Same(sp.GetRequiredService<CoverHashCache>(), factory.Services.GetRequiredService<CoverHashCache>());
    }
}
