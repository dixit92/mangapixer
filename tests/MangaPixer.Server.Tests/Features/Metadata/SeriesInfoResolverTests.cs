namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Xunit;

/// <summary>
/// Service-with-DB tests for <c>SeriesInfoResolver</c> (1.24.0): the walk from
/// self (nearest link, Don't match stops inheritance, 64 bound), ComicInfo
/// aggregation incl. mixed, precedence resolution and the per-field merge, and the
/// "Show series information" toggle.
/// </summary>
public sealed class SeriesInfoResolverTests
{
    private static ComicInfoCreator Writer(string name) => new() { Name = name, Role = "writer" };

    [Fact]
    public async Task NodeWithNothing_IsNone()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Plain");

        var info = await t.ResolveAsync(folder);

        Assert.Equal(SeriesInfoState.None, info.State);
        Assert.Null(info.Title);
        Assert.Equal(folder.PublicId, info.AnchorNodeId);
        Assert.Equal(MetadataPrecedenceSource.Default, info.PrecedenceSource);
    }

    [Fact]
    public async Task Archive_OwnComicInfo_GivesItemFields_AndAnchorsOnAnAgreeingFolder()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Saga Folder");
        var a1 = await t.AddArchiveAsync(folder, "Saga 01");
        var a2 = await t.AddArchiveAsync(folder, "Saga 02");
        await t.AddComicInfoAsync(a1, "Synthetic Saga", number: "1", volume: 1, year: 2020, title: "First");
        await t.AddComicInfoAsync(a2, "Synthetic Saga", number: "2", volume: 1, year: 2021, title: "Second");

        var info = await t.ResolveAsync(a2);

        Assert.Equal(SeriesInfoState.ComicInfo, info.State);
        Assert.Equal("Synthetic Saga", info.Title);
        Assert.Equal("2", info.Item!.Number);
        Assert.Equal("Second", info.Item.Title);
        Assert.Equal("Summary of Second", info.Item.Summary);
        Assert.Equal(folder.PublicId, info.AnchorNodeId);
        Assert.Equal(CatalogNodeKind.Folder, info.AnchorKind);
        Assert.Equal(MetadataFieldSource.ComicInfo, info.FieldSources["title"]);
    }

    [Fact]
    public async Task Archive_InAMixedFolder_AnchorsOnItself()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var magazine = await t.AddFolderAsync(null, "Magazine");
        var a = await t.AddArchiveAsync(magazine, "Issue A");
        var b = await t.AddArchiveAsync(magazine, "Issue B");
        await t.AddComicInfoAsync(a, "Series A");
        await t.AddComicInfoAsync(b, "Series B");

        var info = await t.ResolveAsync(a);

        Assert.Equal(SeriesInfoState.ComicInfo, info.State);
        Assert.Equal(a.PublicId, info.AnchorNodeId);
    }

    [Fact]
    public async Task Folder_AggregatesItsArchives()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Folder");
        var a1 = await t.AddArchiveAsync(folder, "v1");
        var a2 = await t.AddArchiveAsync(folder, "v2");
        var a3 = await t.AddArchiveAsync(folder, "v3");
        await t.AddArchiveAsync(folder, "no-ci");
        await t.AddComicInfoAsync(a1, "Synthetic Saga", year: 2012, creators: [Writer("Writer A")], genres: ["Action"]);
        await t.AddComicInfoAsync(a2, "synthetic saga", year: 2010, creators: [Writer("Writer A")], genres: ["Action", "Drama"]);
        await t.AddComicInfoAsync(a3, "Synthetic Saga", year: 2011, creators: [Writer("Writer B")]);

        var info = await t.ResolveAsync(folder, includeItems: true);

        Assert.Equal(SeriesInfoState.ComicInfo, info.State);
        Assert.Equal("Synthetic Saga", info.Title);
        Assert.Equal(2010, info.StartYear);
        Assert.Equal(3, info.ComicInfo!.ItemsWithComicInfo);
        Assert.Equal(4, info.ComicInfo.ItemsTotal);
        Assert.Equal(10, info.ComicInfo.Count);
        Assert.Equal("Writer A", info.Creators[0].Name); // most frequent first
        Assert.Equal(2, info.Creators.Count);
        Assert.Equal(["Action", "Drama"], info.Genres);
        Assert.Equal(["v1", "v2", "v3"], info.Items.Select(i => i.DisplayName));
        Assert.Null(info.Item);
    }

    [Fact]
    public async Task Folder_WithoutMajority_IsMixed_WithCounts()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Anthology");
        foreach (var (name, series) in new[] { ("1", "Alpha"), ("2", "Alpha"), ("3", "Beta"), ("4", "Beta"), ("5", "Gamma") })
            await t.AddComicInfoAsync(await t.AddArchiveAsync(folder, name), series);

        var info = await t.ResolveAsync(folder);

        Assert.Equal(SeriesInfoState.Mixed, info.State);
        Assert.Null(info.Title);
        Assert.Equal(3, info.MixedSeries.Count);
        Assert.Equal(2, info.MixedSeries[0].Count);
    }

    [Fact]
    public async Task Folder_WithSixtyPercentMajority_IsOneSeries()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Mostly One");
        foreach (var (name, series) in new[] { ("1", "Alpha"), ("2", "Alpha"), ("3", "Alpha"), ("4", "Beta"), ("5", "Gamma") })
            await t.AddComicInfoAsync(await t.AddArchiveAsync(folder, name), series);

        var info = await t.ResolveAsync(folder);

        Assert.Equal(SeriesInfoState.ComicInfo, info.State);
        Assert.Equal("Alpha", info.Title);
    }

    [Fact]
    public async Task Folder_WithOnlySubfolders_AggregatesOneLevelDeeper()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var series = await t.AddFolderAsync(null, "Series");
        var volumes = await t.AddFolderAsync(series, "Volumes");
        await t.AddComicInfoAsync(await t.AddArchiveAsync(volumes, "v1"), "Deep Saga");

        var info = await t.ResolveAsync(series);

        Assert.Equal(SeriesInfoState.ComicInfo, info.State);
        Assert.Equal("Deep Saga", info.Title);
    }

    [Fact]
    public async Task StaleComicInfo_FromAnOldContentVersion_IsIgnored()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Folder");
        var archive = await t.AddArchiveAsync(folder, "v1", contentVersion: 2);
        await t.AddComicInfoAsync(archive, "Old Name", contentVersion: 1);

        Assert.Equal(SeriesInfoState.None, (await t.ResolveAsync(archive)).State);
        Assert.Equal(SeriesInfoState.None, (await t.ResolveAsync(folder)).State);
    }

    [Fact]
    public async Task OwnWebLink_GivesWebState_WithAttribution()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Folder");
        var record = await t.AddRecordAsync("100", "Web Title");
        await t.AddLinkAsync(folder, record);

        var info = await t.ResolveAsync(folder);

        Assert.Equal(SeriesInfoState.Web, info.State);
        Assert.Equal("Web Title", info.Title);
        Assert.Equal("A synthetic web description.", info.Description);
        Assert.Equal(MetadataOrigin.Japan, info.Origin);
        Assert.Equal(MetadataFormat.Comic, info.Format);
        Assert.False(info.Webtoon);
        Assert.Equal(12, info.OriginVolumes);
        Assert.Equal("mangaupdates", info.Web!.Provider);
        Assert.Equal("mangaupdates", info.Web.ProviderName); // no provider registered in B1
        Assert.False(info.Web.HasImage);
        Assert.False(info.Link!.Inherited);
        Assert.Equal(MetadataFieldSource.Web, info.FieldSources["title"]);
        Assert.Equal(MetadataFieldSource.Web, info.FieldSources["origin"]);
    }

    [Fact]
    public async Task AncestorLink_IsInherited_AndAnchoredOnTheHolder()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var series = await t.AddFolderAsync(null, "Series");
        var volumes = await t.AddFolderAsync(series, "Volumes");
        var archive = await t.AddArchiveAsync(volumes, "v1");
        await t.AddLinkAsync(series, await t.AddRecordAsync("200", "Inherited Title"));

        var info = await t.ResolveAsync(archive);

        Assert.Equal(SeriesInfoState.Web, info.State);
        Assert.Equal("Inherited Title", info.Title);
        Assert.True(info.Link!.Inherited);
        Assert.Equal(series.PublicId, info.Link.NodeId);
        Assert.Equal(series.PublicId, info.AnchorNodeId);
    }

    [Fact]
    public async Task DontMatch_StopsInheritance()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var artist = await t.AddFolderAsync(null, "Artist");
        var work = await t.AddFolderAsync(artist, "Work");
        await t.AddLinkAsync(artist, await t.AddRecordAsync("300", "Wrong Level"));
        await t.AddLinkAsync(work, null, SeriesLinkState.DontMatch);
        var archive = await t.AddArchiveAsync(work, "one-shot");

        var info = await t.ResolveAsync(archive);

        Assert.Equal(SeriesInfoState.DontMatch, info.State);
        Assert.Null(info.Web);
        Assert.Equal(SeriesLinkState.DontMatch, info.Link!.State);
        Assert.True(info.Link.Inherited);
    }

    [Fact]
    public async Task DontMatch_StillShowsComicInfo()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Folder");
        await t.AddComicInfoAsync(await t.AddArchiveAsync(folder, "v1"), "Local Series");
        await t.AddLinkAsync(folder, null, SeriesLinkState.DontMatch);

        var info = await t.ResolveAsync(folder);

        Assert.Equal(SeriesInfoState.ComicInfo, info.State);
        Assert.Equal("Local Series", info.Title);
        Assert.Equal(SeriesLinkState.DontMatch, info.Link!.State);
    }

    [Fact]
    public async Task NearestLink_Wins_OverAnAncestorLink()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var parent = await t.AddFolderAsync(null, "Parent");
        var child = await t.AddFolderAsync(parent, "Child");
        await t.AddLinkAsync(parent, await t.AddRecordAsync("400", "Parent Series"));
        await t.AddLinkAsync(child, await t.AddRecordAsync("401", "Child Series"));

        Assert.Equal("Child Series", (await t.ResolveAsync(child)).Title);
    }

    [Fact]
    public async Task Walk_IsBoundedTo64Ancestors()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var root = await t.AddFolderAsync(null, "Root");
        await t.AddLinkAsync(root, await t.AddRecordAsync("500", "Far Away"));
        var node = root;
        for (var i = 0; i < 64; i++)
            node = await t.AddFolderAsync(node, $"L{i}");
        var within = node;                    // root is exactly 64 levels above
        var beyond = await t.AddFolderAsync(node, "L64"); // root is 65 levels above

        Assert.Equal(SeriesInfoState.Web, (await t.ResolveAsync(within)).State);
        Assert.Equal(SeriesInfoState.None, (await t.ResolveAsync(beyond)).State);
    }

    [Fact]
    public async Task WebFirst_Default_TakesWebFields_AndFallsBackPerField()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Folder");
        await t.AddComicInfoAsync(await t.AddArchiveAsync(folder, "v1"), "CI Title", year: 1990, genres: ["Local Genre"], creators: [Writer("CI Writer")]);
        await t.AddLinkAsync(folder, await t.AddRecordAsync("600", "Web Title", genresJson: null, startYear: null));

        var info = await t.ResolveAsync(folder);

        Assert.Equal(SeriesInfoState.WebAndComicInfo, info.State);
        Assert.Equal("Web Title", info.Title);
        Assert.Equal(MetadataFieldSource.Web, info.FieldSources["title"]);
        Assert.Equal(["Local Genre"], info.Genres); // web has none -> ComicInfo fills the gap
        Assert.Equal(MetadataFieldSource.ComicInfo, info.FieldSources["genres"]);
        Assert.Equal(1990, info.StartYear);
        Assert.Equal(MetadataFieldSource.ComicInfo, info.FieldSources["startYear"]);
        Assert.Equal("Web Author", info.Creators.Single().Name);
        Assert.Equal(MetadataPrecedence.WebFirst, info.Precedence);
    }

    [Fact]
    public async Task LibraryComicInfoFirst_PrefersComicInfo()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var lib = await t.Db.FindAsync<LibraryEntity>(t.LibraryId);
        lib!.MetadataPrecedence = (int)MetadataPrecedence.ComicInfoFirst;
        await t.Db.SaveChangesAsync();
        var folder = await t.AddFolderAsync(null, "Folder");
        await t.AddComicInfoAsync(await t.AddArchiveAsync(folder, "v1"), "CI Title");
        await t.AddLinkAsync(folder, await t.AddRecordAsync("700", "Web Title"));

        var info = await t.ResolveAsync(folder);

        Assert.Equal("CI Title", info.Title);
        Assert.Equal(MetadataFieldSource.ComicInfo, info.FieldSources["title"]);
        Assert.Equal("A synthetic web description.", info.Description); // ComicInfo has none -> web
        Assert.Equal(MetadataPrecedence.ComicInfoFirst, info.Precedence);
        Assert.Equal(MetadataPrecedenceSource.Library, info.PrecedenceSource);
    }

    [Fact]
    public async Task FolderOverride_OnAnAncestor_BeatsTheLibraryValue()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var lib = await t.Db.FindAsync<LibraryEntity>(t.LibraryId);
        lib!.MetadataPrecedence = (int)MetadataPrecedence.WebFirst;
        var parent = await t.AddFolderAsync(null, "Parent");
        var child = await t.AddFolderAsync(parent, "Child");
        t.Db.FolderMetadataPrecedences.Add(new FolderMetadataPrecedenceEntity { NodeId = parent.Id, Precedence = (int)MetadataPrecedence.ComicInfoFirst });
        await t.Db.SaveChangesAsync();

        var info = await t.ResolveAsync(child);

        Assert.Equal(MetadataPrecedence.ComicInfoFirst, info.Precedence);
        Assert.Equal(MetadataPrecedenceSource.Folder, info.PrecedenceSource);
    }

    [Fact]
    public async Task PerItemFields_AlwaysComeFromComicInfo_EvenWebFirst()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Folder");
        var archive = await t.AddArchiveAsync(folder, "v1");
        await t.AddComicInfoAsync(archive, "CI Title", number: "7", title: "Issue Seven");
        await t.AddLinkAsync(folder, await t.AddRecordAsync("800", "Web Title"));

        var info = await t.ResolveAsync(archive);

        Assert.Equal(SeriesInfoState.WebAndComicInfo, info.State);
        Assert.Equal("Web Title", info.Title);
        Assert.Equal("7", info.Item!.Number);
        Assert.Equal("Issue Seven", info.Item.Title);
    }

    [Fact]
    public async Task ShowSeriesInfoOff_Globally_HidesEverything()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Folder");
        await t.AddComicInfoAsync(await t.AddArchiveAsync(folder, "v1"), "Hidden");
        await t.AddLinkAsync(folder, await t.AddRecordAsync("900", "Hidden Web"));
        Assert.Null(await t.Settings().UpdateAsync(new UpdateMetadataSettingsRequest { ShowSeriesInfo = false }, "admin"));

        var info = await t.ResolveAsync(folder);

        Assert.Equal(SeriesInfoState.None, info.State);
        Assert.Null(info.Title);
        Assert.Null(info.Web);
        Assert.Null(info.ComicInfo);
        // The data stays stored.
        Assert.Equal(1, t.Db.MetadataRecords.Count());
        Assert.Equal(1, t.Db.EmbeddedMetadata.Count());
    }

    [Fact]
    public async Task ShowSeriesInfoOff_PerLibrary_HidesOnlyThatLibrary()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var other = await t.AddLibraryAsync("otherlib", "Other");
        var hiddenFolder = await t.AddFolderAsync(null, "Hidden");
        var shownFolder = await t.AddFolderAsync(null, "Shown", other.Id);
        await t.AddComicInfoAsync(await t.AddArchiveAsync(hiddenFolder, "v1"), "A");
        await t.AddComicInfoAsync(await t.AddArchiveAsync(shownFolder, "v1"), "B");
        Assert.True(await t.Settings().UpdateLibraryAsync(t.LibraryPublicId, new UpdateMetadataLibraryRequest { ShowSeriesInfo = false }, "admin"));

        Assert.Equal(SeriesInfoState.None, (await t.ResolveAsync(hiddenFolder)).State);
        Assert.Equal(SeriesInfoState.ComicInfo, (await t.ResolveAsync(shownFolder)).State);
    }

    [Fact]
    public async Task ComicInfoWebLinks_AreAllowlistedOnly()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Folder");
        await t.AddComicInfoAsync(await t.AddArchiveAsync(folder, "v1"), "Linked",
            web: ["https://www.mangaupdates.com/series/abc/x", "https://tracker.example/evil"]);

        var info = await t.ResolveAsync(folder);

        Assert.Equal(["https://www.mangaupdates.com/series/abc/x"], info.ComicInfo!.WebLinks);
    }
}
