namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using System.Data.Common;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

/// <summary>
/// Service-with-DB tests for <see cref="CatalogNodeDto.HasSeriesInfo"/> in browse
/// (1.24.0): which cards get the (i), the "Show series information" toggle, and
/// the performance contract - a fixed number of queries per page, never one per card.
/// </summary>
public sealed class HasSeriesInfoBrowseTests
{
    private static async Task<(UserEntity Admin, CatalogBrowseService Browse)> BrowseAsAdminAsync(MetadataTestDb t)
    {
        var admin = await t.Db.Users.FirstOrDefaultAsync(u => u.IsAdmin) ?? await t.AddUserAsync("admin", isAdmin: true);
        return (admin, new CatalogBrowseService(t.Db, new LibraryAuthorizationService(t.Db)));
    }

    [Fact]
    public async Task Cards_GetTheFlag_OnlyForTheirOwnInformation()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var ciFolder = await t.AddFolderAsync(null, "A CI Folder");
        await t.AddComicInfoAsync(await t.AddArchiveAsync(ciFolder, "v1"), "Series");
        var deepFolder = await t.AddFolderAsync(null, "B Deep Folder");
        await t.AddComicInfoAsync(await t.AddArchiveAsync(await t.AddFolderAsync(deepFolder, "Volumes"), "v1"), "Deep");
        var linkedFolder = await t.AddFolderAsync(null, "C Linked Folder");
        await t.AddLinkAsync(linkedFolder, await t.AddRecordAsync("1", "Linked"));
        var dontFolder = await t.AddFolderAsync(null, "D Dont Folder");
        await t.AddLinkAsync(dontFolder, null, SeriesLinkState.DontMatch);
        var plainFolder = await t.AddFolderAsync(null, "E Plain Folder");
        await t.AddArchiveAsync(plainFolder, "no-ci");
        var ciArchive = await t.AddArchiveAsync(null, "F CI Archive");
        await t.AddComicInfoAsync(ciArchive, "Own");
        var staleArchive = await t.AddArchiveAsync(null, "G Stale Archive", contentVersion: 2);
        await t.AddComicInfoAsync(staleArchive, "Old", contentVersion: 1);
        var (admin, browse) = await BrowseAsAdminAsync(t);

        var page = await browse.BrowseAsync(admin.Id, t.LibraryId, parentId: null, cursor: null, pageSize: 50);
        var flags = page.Items.ToDictionary(i => i.DisplayName, i => i.HasSeriesInfo);

        Assert.True(flags["A CI Folder"]);
        Assert.True(flags["B Deep Folder"]);
        Assert.True(flags["C Linked Folder"]);
        Assert.False(flags["D Dont Folder"]);
        Assert.False(flags["E Plain Folder"]);
        Assert.True(flags["F CI Archive"]);
        Assert.False(flags["G Stale Archive"]);

        // Inside the linked folder: an inherited link alone gives no (i).
        await t.AddArchiveAsync(linkedFolder, "inherits");
        var inner = await browse.BrowseAsync(admin.Id, t.LibraryId, parentId: linkedFolder.Id, cursor: null, pageSize: 50);
        Assert.False(inner.Items.Single().HasSeriesInfo);
    }

    [Fact]
    public async Task ShowSeriesInfoOff_ClearsEveryFlag()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Folder");
        await t.AddComicInfoAsync(await t.AddArchiveAsync(folder, "v1"), "Series");
        var (admin, browse) = await BrowseAsAdminAsync(t);
        Assert.True((await browse.BrowseAsync(admin.Id, t.LibraryId, null, null, 50)).Items.Single().HasSeriesInfo);

        await t.Settings().UpdateLibraryAsync(t.LibraryPublicId, new UpdateMetadataLibraryRequest { ShowSeriesInfo = false }, "admin");
        Assert.False((await browse.BrowseAsync(admin.Id, t.LibraryId, null, null, 50)).Items.Single().HasSeriesInfo);

        await t.Settings().UpdateLibraryAsync(t.LibraryPublicId, new UpdateMetadataLibraryRequest { ShowSeriesInfo = true }, "admin");
        await t.Settings().UpdateAsync(new UpdateMetadataSettingsRequest { ShowSeriesInfo = false }, "admin");
        Assert.False((await browse.BrowseAsync(admin.Id, t.LibraryId, null, null, 50)).Items.Single().HasSeriesInfo);
    }

    /// <summary>
    /// Performance contract: the flag costs the same number of SQL commands for a
    /// 500-card page as for a 20-card page (batched, no per-card query). Command
    /// counts are deterministic, unlike wall-clock asserts.
    /// </summary>
    [Fact]
    public async Task LargePage_UsesAFixedNumberOfQueries_NotOnePerCard()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        for (var i = 0; i < 500; i++)
        {
            var folder = await t.AddFolderAsync(null, $"Folder {i:D3}");
            if (i % 2 == 0)
                await t.AddComicInfoAsync(await t.AddArchiveAsync(folder, "v1"), $"Series {i}");
        }
        var admin = await t.AddUserAsync("admin", isAdmin: true);

        var small = await CountCommandsAsync(t, admin.Id, pageSize: 20);
        var large = await CountCommandsAsync(t, admin.Id, pageSize: 500);

        Assert.Equal(500, large.Page.Items.Count);
        Assert.Equal(250, large.Page.Items.Count(i => i.HasSeriesInfo));
        Assert.Equal(small.Commands, large.Commands);
    }

    private static async Task<(int Commands, PageResponse<CatalogNodeDto> Page)> CountCommandsAsync(MetadataTestDb t, long adminId, int pageSize)
    {
        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(t.Db.Database.GetConnectionString())
            .AddInterceptors(counter)
            .Options;
        await using var db = new MangaPixerDbContext(options);
        var browse = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
        var page = await browse.BrowseAsync(adminId, t.LibraryId, parentId: null, cursor: null, pageSize: pageSize);
        return (counter.Count, page);
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
