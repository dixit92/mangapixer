namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using System.Data.Common;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Home;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

/// <summary>
/// Service-with-DB tests for <see cref="SeriesInfoFlagService"/> (1.28.0): the anchor-aware
/// flag behind the (i) on the Home rows agrees with <see cref="SeriesInfoResolver"/> for every
/// archive shape, honours "Show series information", costs a fixed number of queries, and
/// reaches both Home builders (Continue reading, New chapters) together with the star.
/// </summary>
public sealed class SeriesInfoFlagServiceTests
{
    [Fact]
    public async Task AnchoredFlag_ForArchives_MatchesTheResolver()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var record = await t.AddRecordAsync("1", "Linked");

        var series = await t.AddFolderAsync(null, "Series");
        await t.AddLinkAsync(series, record);
        var inSeries = await t.AddArchiveAsync(series, "in series");
        var volumes = await t.AddFolderAsync(series, "Volumes");
        var deep = await t.AddArchiveAsync(volumes, "inherited from the grandparent");
        var dontFolder = await t.AddFolderAsync(series, "Extras");
        await t.AddLinkAsync(dontFolder, null, SeriesLinkState.DontMatch);
        var underDont = await t.AddArchiveAsync(dontFolder, "below dont match");
        var reviewFolder = await t.AddFolderAsync(series, "Review");
        await t.AddLinkAsync(reviewFolder, await t.AddRecordAsync("2", "Candidate"), SeriesLinkState.NeedsReview);
        var underReview = await t.AddArchiveAsync(reviewFolder, "below needs review");
        var ownDont = await t.AddArchiveAsync(series, "own dont match");
        await t.AddLinkAsync(ownDont, null, SeriesLinkState.DontMatch);

        var loose = await t.AddArchiveAsync(null, "loose, nothing");
        var looseCi = await t.AddArchiveAsync(null, "loose, own ComicInfo");
        await t.AddComicInfoAsync(looseCi, "Own");
        var ciFolder = await t.AddFolderAsync(null, "CI folder");
        await t.AddComicInfoAsync(await t.AddArchiveAsync(ciFolder, "sibling with CI"), "Sib");
        var siblingOnly = await t.AddArchiveAsync(ciFolder, "only its sibling has CI");
        var autoOwn = await t.AddArchiveAsync(null, "own auto link");
        await t.AddLinkAsync(autoOwn, await t.AddRecordAsync("3", "Auto"), SeriesLinkState.Auto);

        var archives = new[] { inSeries, deep, underDont, underReview, ownDont, loose, looseCi, siblingOnly, autoOwn };
        var flags = await new SeriesInfoFlagService(t.Db).WithAnchoredSeriesInfoAsync(archives.Select(a => a.PublicId));

        Assert.Contains(inSeries.PublicId, flags);
        Assert.Contains(deep.PublicId, flags);
        Assert.DoesNotContain(underDont.PublicId, flags);
        Assert.Contains(underReview.PublicId, flags);
        Assert.DoesNotContain(ownDont.PublicId, flags);
        Assert.DoesNotContain(loose.PublicId, flags);
        Assert.Contains(looseCi.PublicId, flags);
        Assert.DoesNotContain(siblingOnly.PublicId, flags);
        Assert.Contains(autoOwn.PublicId, flags);

        // The same answer GET /nodes/{id}/series-info gives, archive by archive.
        foreach (var archive in archives)
        {
            var info = await t.ResolveAsync(archive);
            var resolverHasInfo = info.State is not SeriesInfoState.None and not SeriesInfoState.DontMatch;
            Assert.True(resolverHasInfo == flags.Contains(archive.PublicId), $"{archive.DisplayName}: resolver {info.State}");
        }
    }

    [Fact]
    public async Task AnchoredFlag_ForFolders_KeepsTheBrowseRule()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var series = await t.AddFolderAsync(null, "Series");
        await t.AddLinkAsync(series, await t.AddRecordAsync("1", "Linked"));
        var inherited = await t.AddFolderAsync(series, "Volumes");
        await t.AddArchiveAsync(inherited, "v1");
        var ciFolder = await t.AddFolderAsync(null, "CI folder");
        await t.AddComicInfoAsync(await t.AddArchiveAsync(ciFolder, "v1"), "Series");

        var service = new SeriesInfoFlagService(t.Db);
        var ids = new[] { series.PublicId, inherited.PublicId, ciFolder.PublicId };
        var anchored = await service.WithAnchoredSeriesInfoAsync(ids);
        var own = await service.WithOwnSeriesInfoAsync(ids);

        Assert.Equal(own.OrderBy(x => x), anchored.OrderBy(x => x));
        Assert.Equal(new[] { series.PublicId, ciFolder.PublicId }.OrderBy(x => x), anchored.OrderBy(x => x));
    }

    [Fact]
    public async Task ShowSeriesInfoOff_PerLibraryAndGlobally_ClearsTheAnchoredFlag()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var otherLib = await t.AddLibraryAsync("metalib2", "Meta Lib 2");
        var series = await t.AddFolderAsync(null, "Series");
        await t.AddLinkAsync(series, await t.AddRecordAsync("1", "Linked"));
        var here = await t.AddArchiveAsync(series, "here");
        var otherSeries = await t.AddFolderAsync(null, "Other", libraryId: otherLib.Id);
        await t.AddLinkAsync(otherSeries, await t.AddRecordAsync("2", "Other"));
        var there = await t.AddArchiveAsync(otherSeries, "there");
        var service = new SeriesInfoFlagService(t.Db);
        var ids = new[] { here.PublicId, there.PublicId };

        Assert.Equal(2, (await service.WithAnchoredSeriesInfoAsync(ids)).Count);

        await t.Settings().UpdateLibraryAsync("metalib2", new UpdateMetadataLibraryRequest { ShowSeriesInfo = false }, "admin");
        Assert.Equal(new[] { here.PublicId }, await service.WithAnchoredSeriesInfoAsync(ids));

        await t.Settings().UpdateAsync(new UpdateMetadataSettingsRequest { ShowSeriesInfo = false }, "admin");
        Assert.Empty(await service.WithAnchoredSeriesInfoAsync(ids));
    }

    /// <summary>
    /// Performance contract: 10 or 300 archives cost the same number of SQL commands
    /// (EF commands counted by an interceptor, plus the one recursive walk).
    /// </summary>
    [Fact]
    public async Task AnchoredFlag_UsesAFixedNumberOfQueries_NotOnePerCard()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var series = await t.AddFolderAsync(null, "Series");
        await t.AddLinkAsync(series, await t.AddRecordAsync("1", "Linked"));
        var ids = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            var parent = i % 3 == 0 ? series : null;
            var archive = await t.AddArchiveAsync(parent, $"a{i:D3}");
            if (i % 3 == 1)
                await t.AddComicInfoAsync(archive, $"S{i}");
            ids.Add(archive.PublicId);
        }

        var small = await CountCommandsAsync(t, ids.Take(10).ToList());
        var large = await CountCommandsAsync(t, ids);

        Assert.Equal(200, large.Flags.Count);
        Assert.Equal(small.Commands, large.Commands);
    }

    [Fact]
    public async Task HomeRows_CarryTheStarAndTheAnchoredFlag()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var admin = await t.AddUserAsync("admin", isAdmin: true);
        var series = await t.AddFolderAsync(null, "Series");
        await t.AddLinkAsync(series, await t.AddRecordAsync("1", "Linked"));
        var chapter = await t.AddArchiveAsync(series, "chapter 1");
        var plain = await t.AddFolderAsync(null, "Plain");
        var plainChapter = await t.AddArchiveAsync(plain, "plain 1");
        var looseCi = await t.AddArchiveAsync(null, "loose with CI");
        await t.AddComicInfoAsync(looseCi, "Own");
        foreach (var item in new[] { chapter, plainChapter, looseCi })
            t.Db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = admin.Id,
                ItemId = item.Id,
                ContentVersion = 1,
                Ordinal = 1,
                State = (int)ReadingState.InProgress,
                Revision = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        foreach (var node in new[] { chapter, plain })
            t.Db.Favorites.Add(new FavoriteEntity { UserId = admin.Id, CatalogNodeId = node.Id, CreatedAt = DateTimeOffset.UtcNow });
        await t.Db.SaveChangesAsync();

        var auth = new LibraryAuthorizationService(t.Db);
        var continueReading = (await new ReadingStateService(t.Db, auth).GetContinueReadingAsync(admin.Id))
            .ToDictionary(e => e.DisplayName);
        Assert.True(continueReading["chapter 1"].HasSeriesInfo);   // anchor: the linked series folder
        Assert.True(continueReading["chapter 1"].IsFavorite);
        Assert.False(continueReading["plain 1"].HasSeriesInfo);
        Assert.False(continueReading["plain 1"].IsFavorite);       // its folder is starred, not the archive
        Assert.True(continueReading["loose with CI"].HasSeriesInfo);

        var byLibrary = await new ReadingStateService(t.Db, auth).GetContinueReadingByLibraryAsync(admin.Id, t.LibraryId);
        Assert.True(byLibrary.Single(e => e.DisplayName == "chapter 1").HasSeriesInfo);

        var stacks = (await new RecentChaptersService(t.Db, auth).GetRecentChaptersAsync(admin.Id))
            .Libraries.Single().Stacks.ToDictionary(s => s.DisplayName);
        Assert.True(stacks["Series"].HasSeriesInfo);
        Assert.False(stacks["Series"].IsFavorite);
        Assert.False(stacks["Plain"].HasSeriesInfo);
        Assert.True(stacks["Plain"].IsFavorite);
        Assert.True(stacks["loose with CI"].HasSeriesInfo);
        Assert.False(stacks["loose with CI"].IsFavorite);

        await t.Settings().UpdateLibraryAsync(t.LibraryPublicId, new UpdateMetadataLibraryRequest { ShowSeriesInfo = false }, "admin");
        Assert.DoesNotContain((await new ReadingStateService(t.Db, auth).GetContinueReadingAsync(admin.Id)), e => e.HasSeriesInfo);
        Assert.DoesNotContain((await new RecentChaptersService(t.Db, auth).GetRecentChaptersAsync(admin.Id)).Libraries.Single().Stacks,
            s => s.HasSeriesInfo);
    }

    private static async Task<(int Commands, HashSet<string> Flags)> CountCommandsAsync(MetadataTestDb t, List<string> ids)
    {
        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(t.Db.Database.GetConnectionString())
            .AddInterceptors(counter)
            .Options;
        await using var db = new MangaPixerDbContext(options);
        var flags = await new SeriesInfoFlagService(db).WithAnchoredSeriesInfoAsync(ids);
        return (counter.Count, flags);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
