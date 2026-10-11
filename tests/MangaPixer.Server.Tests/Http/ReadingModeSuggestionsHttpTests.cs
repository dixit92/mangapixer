namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Reading.Suggestions;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) of the reading-mode suggestions (1.41.0): admin-only, the antiforgery check on Apply, Apply's
/// request validation, and the whole path - a tall-strip folder is listed, Apply writes its folder default, the reader resolves the
/// folder's archives to Vertical and the folder leaves the list. Local only: the host fails on any outgoing request.
/// </summary>
[Trait("Category", "Http")]
public sealed class ReadingModeSuggestionsHttpTests : IClassFixture<ReadingModeSuggestionsHttpTests.Host>
{
    private const string Route = "/api/v1/admin/reading-mode-suggestions";

    /// <summary>One host for the class; it must never contact a provider (every signal is local).</summary>
    public sealed class Host : IDisposable
    {
        public MetadataNetworkWebApplicationFactory Factory { get; } = new(failOnAnyRequest: true);

        public void Dispose() => Factory.Dispose();
    }

    private readonly MetadataNetworkWebApplicationFactory _factory;

    public ReadingModeSuggestionsHttpTests(Host host) => _factory = host.Factory;

    /// <summary>A library (default Right to left) with one folder of two archives of 15 tall pages each. Returns (folder, an archive).</summary>
    private async Task<(string Folder, string Archive)> SeedTallFolderAsync(string prefix)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var lib = new LibraryEntity
        {
            PublicId = prefix + "lib",
            DisplayName = "Lib " + prefix,
            RootPath = "/synthetic/" + prefix,
            CreatedAt = DateTimeOffset.UtcNow,
            DefaultReaderMode = (int)ReaderMode.PagedRtl,
        };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var folder = new CatalogNodeEntity
        {
            PublicId = prefix + "f",
            LibraryId = lib.Id,
            Kind = (int)CatalogNodeKind.Folder,
            DisplayName = "Strip Story " + prefix,
            RelativePath = prefix,
            PathKey = prefix,
            SortKey = "0" + prefix,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(folder);
        await db.SaveChangesAsync();
        var archives = new List<CatalogNodeEntity>();
        for (var a = 0; a < 2; a++)
        {
            var archive = new CatalogNodeEntity
            {
                PublicId = $"{prefix}a{a}",
                LibraryId = lib.Id,
                ParentId = folder.Id,
                Kind = (int)CatalogNodeKind.Archive,
                DisplayName = $"c00{a}",
                RelativePath = $"{prefix}/c00{a}.cbz",
                PathKey = $"{prefix}/c00{a}.cbz",
                SortKey = $"1c00{a}",
                CreatedAt = DateTimeOffset.UtcNow,
                ArchiveItem = new ArchiveItemEntity { ContentVersion = 1, AnalysisState = 0, PageCount = 15, ByteLength = 4096 },
            };
            db.CatalogNodes.Add(archive);
            await db.SaveChangesAsync();
            for (var i = 0; i < 15; i++)
                db.PageEntries.Add(new PageEntryEntity
                {
                    ItemId = archive.Id,
                    ContentVersion = 1,
                    Ordinal = i,
                    EntryKey = new PageEntryKey(i).ToOpaque(),
                    SourceEntryLocator = $"p{i}.png",
                    MediaType = "image/png",
                    Width = 100,
                    Height = 500,
                    ByteSize = 100,
                });
            archives.Add(archive);
        }
        await db.SaveChangesAsync();
        return (folder.PublicId, archives[0].PublicId);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Endpoints_AreAdminOnly()
    {
        using var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(Route)).StatusCode);
        var reader = await _factory.CreateReaderClientAsync("rmreader", null);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Route + "/apply", new ReadingModeApplyRequest
        {
            Items = [new() { NodeId = "x", ReaderMode = ReaderMode.VerticalWebtoon }],
        })).StatusCode);
    }

    [Fact]
    public async Task Apply_WithoutTheAntiforgeryHeader_IsRefused()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var (folder, _) = await SeedTallFolderAsync("rmcsrf");
        var token = admin.DefaultRequestHeaders.GetValues("X-MangaPixer-Csrf").Single();
        admin.DefaultRequestHeaders.Remove("X-MangaPixer-Csrf");
        try
        {
            var response = await admin.PostAsJsonAsync(Route + "/apply", new ReadingModeApplyRequest
            {
                Items = [new() { NodeId = folder, ReaderMode = ReaderMode.VerticalWebtoon }],
            });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            admin.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", token);
        }
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        Assert.False(await db.FolderReaderDefaults.AnyAsync(f => f.Node!.PublicId == folder));
    }

    [Theory]
    [InlineData("""{"items":[{"nodeId":"rmvalf","readerMode":7}]}""", "invalid_mode")]
    [InlineData("""{"items":[{"nodeId":"rmvalf","readerMode":"VerticalWebtoon"},{"nodeId":"rmvalf","readerMode":"PagedRtl"}]}""", "duplicate_items")]
    [InlineData("""{"items":[]}""", "no_items")]
    [InlineData("""{"items":[{"nodeId":"rmvalf","readerMode":"Sideways"}]}""", null)]
    [InlineData("""{}""", null)]
    public async Task Apply_ValidatesTheRequest(string body, string? error)
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        if (!await FolderExistsAsync("rmvalf"))
            await SeedTallFolderAsync("rmval");

        var response = await admin.PostAsync(Route + "/apply", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        if (error is not null)
            Assert.Equal(error, (await response.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        Assert.False(await FolderExistsWithDefaultAsync("rmvalf"));
    }

    private async Task<bool> FolderExistsAsync(string publicId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().CatalogNodes.AnyAsync(n => n.PublicId == publicId);
    }

    private async Task<bool> FolderExistsWithDefaultAsync(string publicId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().FolderReaderDefaults.AnyAsync(f => f.Node!.PublicId == publicId);
    }

    [Fact]
    public async Task ATallStripFolder_IsListed_AppliedAndThenReadVertically()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var (folder, archive) = await SeedTallFolderAsync("rmflow");

        var before = await admin.GetFromJsonAsync<EffectiveReaderModeDto>($"/api/v1/reading/{archive}/effective-mode", TestJson.Web);
        Assert.Equal(ReaderMode.PagedRtl, before!.ReaderMode);

        var list = await admin.GetFromJsonAsync<ReadingModeSuggestionsDto>(Route, TestJson.Web);
        var row = Assert.Single(list!.Items, r => r.NodeId == folder);
        Assert.Equal(ReaderMode.VerticalWebtoon, row.SuggestedMode);
        Assert.Equal(ReaderMode.PagedRtl, row.CurrentMode);
        Assert.Equal(ReaderModeSource.Library, row.CurrentSource);
        Assert.Equal(ReadingModeConfidence.High, row.Confidence);
        Assert.Equal("Strip Story rmflow", row.FolderName);

        var applied = await admin.PostAsJsonAsync(Route + "/apply", new ReadingModeApplyRequest
        {
            Items = [new() { NodeId = folder, ReaderMode = row.SuggestedMode }, new() { NodeId = archive, ReaderMode = row.SuggestedMode }],
        });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var result = await applied.Content.ReadFromJsonAsync<ReadingModeApplyResultDto>(TestJson.Web);
        Assert.Equal(1, result!.Applied);
        Assert.Equal("not_a_folder", Assert.Single(result.Skipped).Reason);

        var after = await admin.GetFromJsonAsync<EffectiveReaderModeDto>($"/api/v1/reading/{archive}/effective-mode", TestJson.Web);
        Assert.Equal(ReaderMode.VerticalWebtoon, after!.ReaderMode);
        var again = await admin.GetFromJsonAsync<ReadingModeSuggestionsDto>(Route, TestJson.Web);
        Assert.DoesNotContain(again!.Items, r => r.NodeId == folder);
        Assert.DoesNotContain(again.SetByAdmin, r => r.NodeId == folder);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var nodeId = await db.CatalogNodes.Where(n => n.PublicId == folder).Select(n => n.Id).SingleAsync();
        Assert.Contains(await db.AuditEvents.Where(e => e.Action == AuditActions.ReadingModeSuggestionsApply).ToListAsync(),
            e => e.Result == "applied_1" && e.TargetItemId == nodeId && e.ActorUserId != null);
    }

    [Fact]
    public void Wiring_TheServiceIsRegistered()
    {
        using var scope = _factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ReadingModeSuggestionService>());
    }
}
