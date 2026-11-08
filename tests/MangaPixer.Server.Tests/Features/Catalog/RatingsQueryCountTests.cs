namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using System.Data.Common;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

/// <summary>
/// Service-with-DB test of the cost of <c>CatalogNodeDto.MyRating</c> (1.52.0 lane R): the rating rides in the favourite lookup's query, so
/// a browse page, the "My rating" sort and the Favorites page issue the same number of EF commands for 12 cards as for 200 (never one per
/// card), and the same number with ratings as without any (no extra query for the rating).
/// </summary>
public sealed class RatingsQueryCountTests
{
    [Fact]
    public async Task MyRating_AddsNoQuery_AndNoQueryPerCard()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var admin = await t.AddUserAsync("admin", isAdmin: true);
        var nodes = new List<CatalogNodeEntity>();
        // One kind on every page (a page of folders asks other questions than a page of archives - not a per-card cost): archives at
        // the library root, so the Favorites list has no stacks either.
        for (var i = 0; i < 200; i++)
            nodes.Add(await t.AddArchiveAsync(null, $"Archive {i:D3}"));
        for (var i = 0; i < nodes.Count; i += 3)
            t.Db.Favorites.Add(new FavoriteEntity { UserId = admin.Id, CatalogNodeId = nodes[i].Id, CreatedAt = DateTimeOffset.UtcNow.AddSeconds(i) });
        await t.Db.SaveChangesAsync();

        var withoutRatings = await CountAllAsync(t, admin.Id);

        for (var i = 0; i < nodes.Count; i += 2)
            t.Db.UserRatings.Add(new UserRatingEntity { UserId = admin.Id, CatalogNodeId = nodes[i].Id, Score = 1 + (i % 10), RatedAt = DateTimeOffset.UtcNow });
        await t.Db.SaveChangesAsync();
        var withRatings = await CountAllAsync(t, admin.Id);

        Assert.Equal(withoutRatings.Commands, withRatings.Commands);
        foreach (var (name, (small, large)) in withRatings.Commands)
            Assert.True(small == large, $"{name}: {small} commands for 12 cards, {large} for 200");

        // The values are the user's own scores.
        var page = withRatings.LargeBrowse;
        Assert.Equal(200, page.Items.Count);
        Assert.Equal(100, page.Items.Count(n => n.MyRating is not null));
        Assert.All(page.Items.Where(n => n.MyRating is not null), n => Assert.InRange(n.MyRating!.Value, 1, 10));
        Assert.Equal(67, page.Items.Count(n => n.IsFavorite));
    }

    private sealed record Counts(Dictionary<string, (int Small, int Large)> Commands, PageResponse<CatalogNodeDto> LargeBrowse);

    private static async Task<Counts> CountAllAsync(MetadataTestDb t, long userId)
    {
        var commands = new Dictionary<string, (int, int)>();
        PageResponse<CatalogNodeDto>? large = null;
        foreach (var sort in new[] { "name", CatalogBrowseService.MyRatingSort })
        {
            var (s, _) = await CountAsync(t, b => b.BrowseAsync(userId, t.LibraryId, null, null, pageSize: 12, sort: sort));
            var (l, page) = await CountAsync(t, b => b.BrowseAsync(userId, t.LibraryId, null, null, pageSize: 200, sort: sort));
            commands["browse " + sort] = (s, l);
            if (sort == "name")
                large = page;
        }
        foreach (var sort in new[] { FavoritesQuery.FavoritedSort, FavoritesQuery.MyRatingSort })
        {
            var query = new FavoritesQuery(Sort: sort);
            var (s, _) = await CountAsync(t, b => b.ListFavoritesAsync(userId, query, null, 12, false, default));
            var (l, _) = await CountAsync(t, b => b.ListFavoritesAsync(userId, query, null, 200, false, default));
            commands["favorites " + sort] = (s, l);
        }
        return new Counts(commands, large!);
    }

    private static async Task<(int Commands, PageResponse<CatalogNodeDto> Page)> CountAsync(
        MetadataTestDb t, Func<CatalogBrowseService, Task<PageResponse<CatalogNodeDto>>> call)
    {
        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(t.Db.Database.GetConnectionString())
            .AddInterceptors(counter)
            .Options;
        await using var db = new MangaPixerDbContext(options);
        var page = await call(new CatalogBrowseService(db, new LibraryAuthorizationService(db)));
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
