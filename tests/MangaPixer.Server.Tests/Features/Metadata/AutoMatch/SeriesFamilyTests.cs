namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of series families in automatic matching (1.30.0, owner: a spin-off decision is not automatic, and the
/// review groups a series with its related records) through the production path: <c>MetadataAutoMatchService.ProcessAsync</c> with
/// the real detector, planner and scorer, and MangaUpdates answers in the recorded shape (the main series lists the subtitled record
/// as its Prequel, which lists the main series as its Sequel). Synthetic titles.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class SeriesFamilyTests : IAsyncLifetime
{
    private const string Main = "Qzv Garden Walk";
    private const string SpinOff = "Qzv Garden Walk - Before the Frost";
    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new AutoMatchHarness(_db);
        await _h.EnableAutomaticAsync();
        MuJson.Hit[] hits = [new MuJson.Hit(902, SpinOff, Year: 2013), new MuJson.Hit(901, Main, Year: 2009)];
        _h.Search[SpinOff] = hits;
        _h.Search[Main] = hits;
        _h.Records[901] = MuJson.Get(901, Main, related: [(902, "Prequel"), (950, "Spin-Off")]);
        _h.Records[902] = MuJson.Get(902, SpinOff, related: [(901, "Sequel")]);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private async Task<CatalogNodeEntity> FolderAsync(string name)
    {
        var folder = await _db.AddFolderAsync(null, name);
        for (var i = 1; i <= 12; i++)
            await _db.AddArchiveAsync(folder, $"{name} - Chapter {i:D3}");
        return folder;
    }

    private async Task<(NodeSeriesLinkEntity Link, MetadataMatchQueueEntity Queue, List<MetadataMatchCandidateEntity> Candidates)> MatchAsync(CatalogNodeEntity folder)
    {
        var service = _h.ServiceWithRealMatcher();
        await service.StartBulkAsync(_db.LibraryPublicId, new Core.Api.MetadataMatchLibraryRequest(), "admin");
        var row = await service.LeaseNextAsync("test");
        await _h.ServiceWithRealMatcher().ProcessAsync(row!);
        _db.Db.ChangeTracker.Clear();
        var link = await _db.Db.NodeSeriesLinks.AsNoTracking().Include(l => l.Record).SingleAsync(l => l.NodeId == folder.Id);
        var queue = await _db.Db.MetadataMatchQueue.AsNoTracking().SingleAsync(q => q.NodeId == folder.Id);
        var candidates = await _db.Db.MetadataMatchCandidates.AsNoTracking().Where(c => c.NodeId == folder.Id).OrderBy(c => c.Rank).ToListAsync();
        return (link, queue, candidates);
    }

    [Fact]
    public async Task AFolderSubtitle_ThatOnlySeparatesTheSpinOffFromItsMainSeries_WaitsInReview_WithTheFamilyStoredTogether()
    {
        var (link, queue, candidates) = await MatchAsync(await FolderAsync(SpinOff));

        Assert.Equal((int)SeriesLinkState.NeedsReview, link.State);
        var reasons = (MatchReason)queue.OutcomeReasons;
        Assert.True(reasons.HasFlag(MatchReason.SubtitleFamily));
        Assert.True(reasons.HasFlag(MatchReason.SeriesFamily));
        // The spin-off stays first (lane M's ranking); the main series is stored although it is 0.20 behind (outside the window).
        Assert.Equal(
            [("902", 1, "prequel"), ("901", 1, "main_story")],
            candidates.Select(c => (c.ExternalId, c.FamilyGroup ?? 0, c.FamilyRole ?? "")));
    }

    [Fact]
    public async Task TheMainSeriesFolder_LinksAutomatically_WithTheFamilyChipOnItsOutcome()
    {
        var (link, queue, candidates) = await MatchAsync(await FolderAsync(Main));

        Assert.Equal((int)SeriesLinkState.Auto, link.State);
        Assert.Equal("901", link.Record!.ExternalId);
        var reasons = (MatchReason)queue.OutcomeReasons;
        Assert.True(reasons.HasFlag(MatchReason.SeriesFamily)); // the Auto-linked tab shows the chip
        Assert.False(reasons.HasFlag(MatchReason.SubtitleFamily));
        Assert.Empty(candidates); // an automatic link stores no candidates
    }
}
