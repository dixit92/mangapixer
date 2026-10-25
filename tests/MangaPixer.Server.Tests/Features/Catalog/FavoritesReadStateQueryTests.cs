namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using System.Data.Common;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

/// <summary>
/// Service-with-DB test for the cost of <see cref="CatalogBrowseService.ListFavoritesAsync"/> (1.48.0 lane U): the folder read rollups and
/// the anchored (i) are batched for the page, so a 12-card home row and a 200-card page issue the same number of EF commands, never one
/// per card. (The rollup itself is one recursive command on the raw connection, whatever the folder count.)
/// </summary>
public sealed class FavoritesReadStateQueryTests
{
    [Fact]
    public async Task ListFavorites_UsesAFixedNumberOfQueries_NotOnePerCard()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var admin = await t.AddUserAsync("admin", isAdmin: true);
        var record = await t.AddRecordAsync("1", "Linked");
        for (var i = 0; i < 200; i++)
        {
            var folder = await t.AddFolderAsync(null, $"Folder {i:D3}");
            if (i % 4 == 0)
                await t.AddLinkAsync(folder, record);
            var archive = await t.AddArchiveAsync(folder, "v1");
            var second = await t.AddArchiveAsync(folder, "v2");
            if (i % 3 == 0)
                t.Db.ReadMarks.Add(new ReadMarkEntity { UserId = admin.Id, ItemId = archive.Id, MarkedAt = DateTimeOffset.UtcNow });
            if (i % 3 == 1)
                t.Db.ReadingProgress.Add(new ReadingProgressEntity
                {
                    UserId = admin.Id,
                    ItemId = second.Id,
                    ContentVersion = 1,
                    Ordinal = 1,
                    State = (int)ReadingState.InProgress,
                    Revision = 1,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            // Alternate a starred folder and a starred archive of it (a single archive favorite, since the folder holds one star).
            var starred = i % 2 == 0 ? folder : archive;
            t.Db.Favorites.Add(new FavoriteEntity { UserId = admin.Id, CatalogNodeId = starred.Id, CreatedAt = DateTimeOffset.UtcNow.AddSeconds(i) });
        }
        await t.Db.SaveChangesAsync();

        var small = await CountCommandsAsync(t, admin.Id, pageSize: 12);
        var large = await CountCommandsAsync(t, admin.Id, pageSize: 200);

        Assert.Equal(12, small.Page.Items.Count);
        Assert.Equal(200, large.Page.Items.Count);
        Assert.Equal(100, large.Page.Items.Count(i => i.ReadRollup is not null));   // every starred folder has archives to roll up
        Assert.Equal(small.Commands, large.Commands);
    }

    private static async Task<(int Commands, PageResponse<CatalogNodeDto> Page)> CountCommandsAsync(MetadataTestDb t, long userId, int pageSize)
    {
        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(t.Db.Database.GetConnectionString())
            .AddInterceptors(counter)
            .Options;
        await using var db = new MangaPixerDbContext(options);
        var browse = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
        var page = await browse.ListFavoritesAsync(userId, pageSize: pageSize);
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
