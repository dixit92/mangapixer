namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Xunit;

/// <summary>
/// Unit tests (1.54.0, community scores): how a fetched record's community score reaches the stored record. MangaUpdates' score comes
/// with its series answer, so every MangaUpdates fetch sets (or clears) it; a MangaDex record's score comes from the separate statistics
/// pass, so a MangaDex GET never touches it, nor the MyAnimeList score kept on the same row.
/// </summary>
public sealed class CommunityScoreStorageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static ProviderSeriesRecord Fetched(string provider, double? score = null, int? votes = null) => new()
    {
        Provider = provider, ExternalId = "x", Title = "Synthetic", CommunityScore = score, CommunityVotes = votes,
    };

    [Fact]
    public void A_MangaUpdatesFetch_StoresItsScore_AndClearsAScoreThatIsGone()
    {
        var record = new MetadataRecordEntity { Provider = "mangaupdates", ExternalId = "x" };
        MetadataIdentifyService.Apply(record, Fetched("mangaupdates", 8.98, 1200), Now);
        Assert.Equal((8.98, 1200, Now), (record.CommunityScore!.Value, record.CommunityVotes!.Value, record.CommunityScoreAt!.Value));

        MetadataIdentifyService.Apply(record, Fetched("mangaupdates"), Now.AddDays(30));
        Assert.Null(record.CommunityScore);
        Assert.Null(record.CommunityVotes);
        Assert.Equal(Now.AddDays(30), record.CommunityScoreAt); // checked: no score now
    }

    [Fact]
    public void A_MangaDexFetch_LeavesTheStatisticsAndMyAnimeListScoresAlone()
    {
        var record = new MetadataRecordEntity
        {
            Provider = "mangadex", ExternalId = "x", CommunityScore = 8.1, CommunityVotes = 300, CommunityScoreAt = Now,
            MalScore = 8.62, MalVotes = 51000, MalScoreAt = Now,
        };
        MetadataIdentifyService.Apply(record, Fetched("mangadex"), Now.AddDays(1));
        Assert.Equal((8.1, 300, Now), (record.CommunityScore!.Value, record.CommunityVotes!.Value, record.CommunityScoreAt!.Value));
        Assert.Equal((8.62, 51000, Now), (record.MalScore!.Value, record.MalVotes!.Value, record.MalScoreAt!.Value));
    }
}
