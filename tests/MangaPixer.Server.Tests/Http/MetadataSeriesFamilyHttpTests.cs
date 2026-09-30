namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
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
/// HTTP test (WebApplicationFactory, the REAL matcher core from DI) of series families on the review dashboard (1.30.0): a folder
/// named <c>Series - Subtitle</c> whose subtitle alone separates a spin-off from its main series goes through a worker pass to Needs
/// review, and the review DTO carries both records as one family with their roles and the reason chips. Synthetic titles; every
/// outbound request ends in the scripted handler.
/// </summary>
[Trait("Category", "Http")]
[Collection("HttpSerial")]
public sealed class MetadataSeriesFamilyHttpTests
{
    private const string LibPub = "sflib1";
    private const string Main = "Qzv Lantern Walk";
    private const string SpinOff = "Qzv Lantern Walk - Before the Frost";

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

    [Fact]
    public async Task ASpinOffDecidedOnlyByTheSubtitle_ReachesReview_WithBothRecordsAsOneFamily()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Family Lib", RootPath = "/synthetic/sf", CreatedAt = DateTimeOffset.UtcNow, MetadataEnabled = true };
            db.Libraries.Add(lib);
            await db.SaveChangesAsync();
            var folder = Node("sfFolder", lib.Id, null, CatalogNodeKind.Folder, SpinOff);
            db.CatalogNodes.Add(folder);
            await db.SaveChangesAsync();
            db.CatalogNodes.AddRange(Enumerable.Range(1, 12).Select(i =>
                Node($"sfArc{i}", lib.Id, folder.Id, CatalogNodeKind.Archive, $"{SpinOff} - Chapter {i:D3}")));
            await db.SaveChangesAsync();
        }
        factory.Handler.Respond = request => request.Method == HttpMethod.Post
            ? ScriptedHandler.Json(MuJson.Search(new MuJson.Hit(902, SpinOff, Year: 2013), new MuJson.Hit(901, Main, Year: 2009)))
            : request.RequestUri!.AbsolutePath.EndsWith("/901", StringComparison.Ordinal)
                ? ScriptedHandler.Json(MuJson.Get(901, Main, related: [(902, "Prequel")]))
                : ScriptedHandler.Json(MuJson.Get(902, SpinOff, related: [(901, "Sequel")]));
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

        var response = await admin.GetAsync($"/api/v1/admin/metadata/review?tab=NeedsReview&library={LibPub}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var raw = await response.Content.ReadAsStringAsync();
        var page = (await response.Content.ReadFromJsonAsync<MetadataReviewPageDto>(TestJson.Web))!;
        var item = Assert.Single(page.Items);
        Assert.Equal("sfFolder", item.NodeId);
        Assert.Contains("subtitle_family", item.Reasons);
        Assert.Contains("series_family", item.Reasons);
        Assert.Equal(
            [(1, "902", 1, "prequel"), (2, "901", 1, "main_story")],
            item.Candidates.Select(c => (c.Rank, c.ExternalId, c.FamilyGroup ?? 0, c.FamilyRole ?? "")));
        Assert.Contains("\"familyRole\":\"main_story\"", raw, StringComparison.Ordinal); // the wire name the web client reads
    }
}
