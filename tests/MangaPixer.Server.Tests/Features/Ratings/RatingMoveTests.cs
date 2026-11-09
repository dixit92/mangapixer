namespace com.lifepixer.mangapixer.Tests.Server.Features.Ratings;

using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Library.Moves;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.52.0 lane R: a user's rating follows a moved folder or archive like a favorite - a rating left on the tombstone would vanish with it
/// when the trash purges the old node. A user who already rated the new copy keeps that rating. Service-with-DB, synthetic files.
/// </summary>
public sealed class RatingMoveTests : IDisposable
{
    private readonly MoveTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task RateAsync(long userId, long nodeId, int score)
    {
        await using var db = _h.NewContext();
        db.UserRatings.Add(new UserRatingEntity { UserId = userId, CatalogNodeId = nodeId, Score = score, RatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<int?> ScoreAsync(long userId, long nodeId)
    {
        await using var db = _h.NewContext();
        return await db.UserRatings.Where(r => r.UserId == userId && r.CatalogNodeId == nodeId).Select(r => (int?)r.Score).FirstOrDefaultAsync();
    }

    [Fact]
    public async Task AFolderRename_CarriesEachUsersRating()
    {
        await _h.InitAsync();
        var library = await _h.AddLibraryAsync("library");
        var reader = await _h.AddUserAsync("reader");
        var other = await _h.AddUserAsync("other");
        _h.WriteArchive(library, "Series/v01.cbz", 1);
        _h.WriteArchive(library, "Series/v02.cbz", 2);
        await _h.ScanAsync(library);
        await _h.AnalyseLibraryAsync(library);
        var folder = await _h.NodeAsync(library, "Series");
        await RateAsync(reader, folder.Id, 9);

        _h.Move(library, "Series", library, "Series (complete)");
        var scan = await _h.ScanAsync(library);
        await using (var db = _h.NewContext())
            Assert.Equal(1, await MoveTestHarness.CarryOver(db).CarryAsync(library, scan.Moves));

        var renamed = await _h.NodeAsync(library, "Series (complete)");
        Assert.Equal(9, await ScoreAsync(reader, renamed.Id));
        Assert.Null(await ScoreAsync(reader, folder.Id));
        Assert.Null(await ScoreAsync(other, renamed.Id));
    }

    [Fact]
    public async Task AnArchivePairedAcrossLibraries_CarriesTheRating_UnlessTheUserRatedTheNewCopy()
    {
        await _h.InitAsync();
        var src = await _h.AddLibraryAsync("ongoing");
        var dst = await _h.AddLibraryAsync("concluded");
        var reader = await _h.AddUserAsync("reader");
        var other = await _h.AddUserAsync("other");
        _h.WriteArchive(src, "Series/v01.cbz", 1);
        _h.WriteArchive(src, "Series/v02.cbz", 2);
        _h.WriteArchive(src, "Series/v03.cbz", 3);
        _h.WriteArchive(src, "Other/x.cbz", 4);
        await _h.ScanAsync(src);
        await _h.ScanAsync(dst);
        await _h.AnalyseLibraryAsync(src);
        var v1 = await _h.NodeAsync(src, "Series/v01.cbz");
        await RateAsync(reader, v1.Id, 6);
        await RateAsync(other, v1.Id, 3);

        _h.Move(src, "Series", dst, "Series");
        await _h.ScanAsync(dst);
        await _h.AnalyseLibraryAsync(dst);
        var n1 = await _h.NodeAsync(dst, "Series/v01.cbz");
        await RateAsync(other, n1.Id, 10);
        await _h.ScanAsync(src);
        Assert.True((await _h.PairAsync()).Paired > 0);

        Assert.Equal(6, await ScoreAsync(reader, n1.Id));
        Assert.Equal(10, await ScoreAsync(other, n1.Id));
        Assert.Equal(3, await ScoreAsync(other, v1.Id));
    }
}
