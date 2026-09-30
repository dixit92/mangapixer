namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Declared;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of declared facts as matching evidence (1.28.0; the type a strong hint since 1.30.0) through the production path: a
/// <c>declared_facts</c> row -> <see cref="DeclaredFactsReader"/> -> <c>MetadataAutoMatchService.ProcessAsync</c> -> the real
/// planner and scorer. Two records share a title and differ only by their author disambiguator: without a declaration
/// the work waits in review; with the library's declared author it links the right record automatically. Synthetic.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class DeclaredEvidenceTests : IAsyncLifetime
{
    private const string Title = "Qzv Harbor Tale";
    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new AutoMatchHarness(_db);
        await _h.EnableAutomaticAsync();
        _h.Search[Title] = [new MuJson.Hit(801, $"{Title} (ALPHA Writer)"), new MuJson.Hit(802, $"{Title} (BETA Painter)")];
        _h.Records[801] = MuJson.Get(801, $"{Title} (ALPHA Writer)");
        _h.Records[802] = MuJson.Get(802, $"{Title} (BETA Painter)");
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private async Task<CatalogNodeEntity> FolderAsync()
    {
        var folder = await _db.AddFolderAsync(null, Title);
        for (var i = 1; i <= 3; i++)
            await _db.AddArchiveAsync(folder, $"{Title} v{i:D2}");
        return folder;
    }

    private async Task DeclareAsync(long? nodeId, string key, string value)
    {
        _db.Db.DeclaredFacts.Add(new DeclaredFactEntity
        {
            LibraryId = _db.LibraryId,
            NodeId = nodeId,
            Key = key,
            Value = value,
            Position = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _db.Db.SaveChangesAsync();
    }

    private async Task<NodeSeriesLinkEntity> MatchAsync(CatalogNodeEntity folder, MetadataAutoMatchOptions? options = null)
    {
        var service = _h.ServiceWithRealMatcher(new DeclaredFactsReader(_db.Db), options);
        await service.StartBulkAsync(_db.LibraryPublicId, new Core.Api.MetadataMatchLibraryRequest(), "admin");
        var row = await service.LeaseNextAsync("test");
        await _h.ServiceWithRealMatcher(new DeclaredFactsReader(_db.Db), options).ProcessAsync(row!);
        _db.Db.ChangeTracker.Clear();
        return await _db.Db.NodeSeriesLinks.AsNoTracking().Include(l => l.Record).SingleAsync(l => l.NodeId == folder.Id);
    }

    [Fact]
    public async Task WithoutADeclaration_TheSameTitledRecordsWaitInReview()
    {
        var link = await MatchAsync(await FolderAsync());

        Assert.Equal((int)SeriesLinkState.NeedsReview, link.State);
    }

    [Fact]
    public async Task TheLibrarysDeclaredAuthor_LinksTheRecordByThatAuthor()
    {
        var folder = await FolderAsync();
        await DeclareAsync(null, DeclaredFactKeys.Creator, "Beta Painter");

        var link = await MatchAsync(folder);

        Assert.Equal((int)SeriesLinkState.Auto, link.State);
        Assert.Equal("802", link.Record!.ExternalId);
        Assert.Equal(0, _h.Handler.Seen.Count(r => r.Body?.Contains("Beta", StringComparison.OrdinalIgnoreCase) == true)); // never sent
    }

    private IReadOnlyList<string> SentFilterTypes() =>
        _h.Handler.Seen.Where(r => r.Method == HttpMethod.Post).Select(r => System.Text.Json.JsonDocument.Parse(r.Body!).RootElement)
            .SelectMany(b => b.GetProperty("filter_types").EnumerateArray().Select(t => t.GetString()!)).Distinct().Order(StringComparer.Ordinal).ToList();

    // Two records share a title with no disambiguator and differ only by origin (1.30.0): the declared type settles the tie.
    private const string OriginTitle = "Qzv Lantern Road";

    private async Task<CatalogNodeEntity> OriginTieFolderAsync()
    {
        _h.Search[OriginTitle] = [new MuJson.Hit(811, OriginTitle, "Manga"), new MuJson.Hit(812, OriginTitle, "Manhwa")];
        _h.Records[811] = MuJson.Get(811, OriginTitle, type: "Manga");
        _h.Records[812] = MuJson.Get(812, OriginTitle, type: "Manhwa");
        var folder = await _db.AddFolderAsync(null, OriginTitle);
        for (var i = 1; i <= 3; i++)
            await _db.AddArchiveAsync(folder, $"{OriginTitle} v{i:D2}");
        return folder;
    }

    [Fact]
    public async Task ADeclaredManhwa_IsAStrongHint_ItSettlesAnOriginTie_AndIsNeverSent()
    {
        var folder = await OriginTieFolderAsync();
        await DeclareAsync(folder.Id, DeclaredFactKeys.Type, DeclaredFactKeys.TypeSlug(DeclaredType.Manhwa));

        var link = await MatchAsync(folder);

        Assert.Equal((int)SeriesLinkState.Auto, link.State);
        Assert.Equal("812", link.Record!.ExternalId);
        // 1.30.0: the declaration no longer narrows the provider type filter - only the fixed four types are sent.
        Assert.Equal(["Artbook", "Doujinshi", "Drama CD", "Novel"], SentFilterTypes());
    }

    [Fact]
    public async Task WithoutADeclaration_AnOriginTieWaitsInReview()
    {
        var link = await MatchAsync(await OriginTieFolderAsync());

        Assert.Equal((int)SeriesLinkState.NeedsReview, link.State);
    }

    [Fact]
    public async Task AWrongDeclaredType_IsNeverAVeto_TheOnlyRecordStillLinks()
    {
        const string title = "Qzv Quiet Orchard";
        _h.Search[title] = [new MuJson.Hit(821, title, "Manhwa")];
        _h.Records[821] = MuJson.Get(821, title, type: "Manhwa");
        var folder = await _db.AddFolderAsync(null, title);
        for (var i = 1; i <= 3; i++)
            await _db.AddArchiveAsync(folder, $"{title} v{i:D2}");
        await DeclareAsync(folder.Id, DeclaredFactKeys.Type, DeclaredFactKeys.TypeSlug(DeclaredType.Manga));

        var link = await MatchAsync(folder);

        Assert.Equal((int)SeriesLinkState.Auto, link.State);
        Assert.Equal("821", link.Record!.ExternalId);
    }

    [Fact]
    public async Task TheRetiredFilterSwitch_IsIgnored()
    {
        // Metadata:AutoMatch:DeclaredTypeFilter (1.28.0 - 1.29.x) is retired: an old value is read without an error and changes nothing.
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Metadata:AutoMatch:DeclaredTypeFilter"] = "true" }).Build();
        var options = MetadataAutoMatchOptions.FromConfiguration(config);
        var folder = await OriginTieFolderAsync();
        await DeclareAsync(folder.Id, DeclaredFactKeys.Type, DeclaredFactKeys.TypeSlug(DeclaredType.Manhwa));

        await MatchAsync(folder, options);

        Assert.Equal(new MetadataAutoMatchOptions(), options);
        Assert.Equal(["Artbook", "Doujinshi", "Drama CD", "Novel"], SentFilterTypes());
    }

    [Fact]
    public async Task ADeclaredWebtoon_AddsNothing()
    {
        var folder = await FolderAsync();
        await DeclareAsync(null, DeclaredFactKeys.Type, DeclaredFactKeys.TypeSlug(DeclaredType.Webtoon));

        await MatchAsync(folder);

        Assert.Equal(["Artbook", "Doujinshi", "Drama CD", "Novel"], SentFilterTypes());
    }

    [Fact]
    public async Task ADeclarationOnAnotherFolder_DoesNotApply()
    {
        var folder = await FolderAsync();
        var other = await _db.AddFolderAsync(null, "Qzv Unrelated Shelf");
        await DeclareAsync(other.Id, DeclaredFactKeys.Creator, "Beta Painter");

        var link = await MatchAsync(folder);

        Assert.Equal((int)SeriesLinkState.NeedsReview, link.State);
    }
}
