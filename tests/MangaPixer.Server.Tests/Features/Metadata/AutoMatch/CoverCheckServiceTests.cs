namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch.LinkCoverCheck;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Covers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Service-with-DB tests for the cover check after linking (1.31.0, <see cref="CoverCheckService"/>): an Auto link whose volume
/// covers are clearly different from the record's stored volume covers in every language drops to Needs review with the
/// <c>cover_differs</c> reason; a matching cover in any language keeps it; Confirmed links, chapter folders, too few volumes,
/// spreads without crops and the "Compare covers" switch are respected; unchanged inputs are not hashed again; the log line carries
/// numbers only. Covers are stored HASHES in files (the cover-layer kit) - no picture, no request.
/// </summary>
public sealed class CoverCheckServiceTests : IAsyncLifetime
{
    // Clearly different from each other (32+ bits apart), see CoverCheckRuleTests.
    private const ulong W1 = 0x0000_0000_0000_0000;
    private const ulong W2 = 0xFFFF_FFFF_0000_0000;
    private const ulong L1 = 0x0000_0000_FFFF_FFFF;
    private const ulong L2 = 0x0000_FFFF_FFFF_0000;

    private CoverLayerTestKit _kit = null!;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly CoverCheckState _state = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync() => _kit = await CoverLayerTestKit.CreateAsync();

    public async Task DisposeAsync() => await _kit.DisposeAsync();

    private static ulong Near(ulong hash, int bits) => hash ^ ((1UL << bits) - 1);

    private CoverCheckService Service() => new(
        _kit.Db.Db, _kit.Hasher, _kit.Thumbnails, _kit.Files, new CoverHashCache(), _state,
        new StoredCoverCompareSetting(_kit.Db.Db, new MetadataAutoMatchOptions()), _time,
        new LoggerFactory([_logs]).CreateLogger<CoverCheckService>());

    /// <summary>An Auto-linked (or <paramref name="state"/>) series folder with one portrait volume archive per local hash.</summary>
    private async Task<(CatalogNodeEntity Folder, MetadataRecordEntity Record, MetadataRecordEntity Companion)> SeriesAsync(
        SeriesLinkState state, params ulong[] volumeHashes)
    {
        var record = await _kit.Db.AddRecordAsync("cc" + Guid.NewGuid().ToString("N")[..6], "Synthetic Linked");
        var folder = await _kit.Db.AddFolderAsync(null, "Synthetic Folder " + record.ExternalId);
        for (var i = 0; i < volumeHashes.Length; i++)
            await _kit.AddBookAsync(folder, $"Synthetic Folder v{i + 1:00}", 800, 1200, volumeHashes[i]);
        await LinkAsync(folder, record, state);
        return (folder, record, await _kit.AddCompanionAsync(record));
    }

    private async Task LinkAsync(CatalogNodeEntity node, MetadataRecordEntity record, SeriesLinkState state)
    {
        await _kit.Db.AddLinkAsync(node, record, state);
        var db = _kit.Db.Db;
        (await db.NodeSeriesLinks.SingleAsync(l => l.NodeId == node.Id)).MatchScore = 0.96;
        db.MetadataMatchQueue.Add(new MetadataMatchQueueEntity
        {
            NodeId = node.Id,
            LibraryId = node.LibraryId,
            State = 2,
            Outcome = (int)MatchBand.Auto,
            EnqueuedAt = DateTimeOffset.UtcNow,
            OutcomeReasons = (int)MatchReason.SeriesFamily,
        });
        await db.SaveChangesAsync();
    }

    private async Task<NodeSeriesLinkEntity> LinkOfAsync(long nodeId)
    {
        _kit.Db.Db.ChangeTracker.Clear();
        return await _kit.Db.Db.NodeSeriesLinks.AsNoTracking().SingleAsync(l => l.NodeId == nodeId);
    }

    [Fact]
    public async Task EveryVolumeClearlyDifferent_InEveryLanguage_DropsTheAutoLinkToReview()
    {
        var (folder, record, md) = await SeriesAsync(SeriesLinkState.Auto, L1, L2);
        await _kit.AddStoredCoverAsync(md, 1, "en", W1);
        await _kit.AddStoredCoverAsync(md, 1, "ja", Near(W1, 3));
        await _kit.AddStoredCoverAsync(md, 2, "ja", W2);

        var result = await Service().SweepAsync();

        Assert.Equal(new CoverCheckSweepResult(1, 1), result);
        var link = await LinkOfAsync(folder.Id);
        Assert.Equal(((int)SeriesLinkState.NeedsReview, (long?)null), (link.State, link.RecordId));
        var candidate = await _kit.Db.Db.MetadataMatchCandidates.SingleAsync(c => c.NodeId == folder.Id);
        Assert.Equal((record.ExternalId, 1, (int)MatchReason.CoverDiffers, 0.96), (candidate.ExternalId, candidate.Rank, candidate.Reasons, candidate.TitleScore));
        var queue = await _kit.Db.Db.MetadataMatchQueue.SingleAsync(q => q.NodeId == folder.Id);
        Assert.Equal((int)MatchBand.NeedsReview, queue.Outcome);
        Assert.Equal(MatchReason.SeriesFamily | MatchReason.CoverDiffers, (MatchReason)queue.OutcomeReasons);
        Assert.Contains(MatchReasonCodes.Of(candidate.Reasons), c => c == "cover_differs");

        // The log line: ids, verdict, counts and distances (numbers) - never a title.
        var line = Assert.Single(_logs.Lines, l => l.Contains("Cover check: node", StringComparison.Ordinal));
        Assert.Contains("Differs, 2 volumes compared (distances 29 32)", line, StringComparison.Ordinal);
        Assert.Contains("moved to review", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASameCover_InAnyLanguage_KeepsTheLink()
    {
        var (folder, _, md) = await SeriesAsync(SeriesLinkState.Auto, L1, Near(W2, 4));
        await _kit.AddStoredCoverAsync(md, 1, "en", W1);
        await _kit.AddStoredCoverAsync(md, 2, "en", L2);
        await _kit.AddStoredCoverAsync(md, 2, "ja", W2);

        Assert.Equal(new CoverCheckSweepResult(1, 0), await Service().SweepAsync());

        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(folder.Id)).State);
        Assert.Contains(_logs.Lines, l => l.Contains("Agrees", StringComparison.Ordinal) && l.Contains("kept", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AConfirmedLink_IsNeverChecked()
    {
        var (folder, record, md) = await SeriesAsync(SeriesLinkState.Confirmed, L1, L2);
        await _kit.AddStoredCoverAsync(md, 1, "en", W1);
        await _kit.AddStoredCoverAsync(md, 2, "en", W2);

        Assert.Equal(new CoverCheckSweepResult(0, 0), await Service().SweepAsync());

        var link = await LinkOfAsync(folder.Id);
        Assert.Equal(((int)SeriesLinkState.Confirmed, (long?)record.Id), (link.State, link.RecordId));
        Assert.Equal(0, _kit.Hasher.Calls);
    }

    [Fact]
    public async Task OneVolumeWithAStoredCover_IsNotEnough_ForAFolderOfVolumes()
    {
        var (folder, _, md) = await SeriesAsync(SeriesLinkState.Auto, L1, L2, L1 ^ 0xFF00);
        await _kit.AddStoredCoverAsync(md, 1, "en", W1);

        await Service().SweepAsync();

        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(folder.Id)).State);
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("Cover check: node", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AOneShot_WithADifferentVolume1Cover_GoesToReview_ButTheSameMainCoverKeepsIt()
    {
        var wrongRecord = await _kit.Db.AddRecordAsync("ccos1", "Synthetic One-shot");
        var wrong = await _kit.AddBookAsync(null, "Synthetic One-shot", 800, 1200, L1);
        await LinkAsync(wrong, wrongRecord, SeriesLinkState.Auto);
        var wrongMd = await _kit.AddCompanionAsync(wrongRecord);
        await _kit.AddStoredCoverAsync(wrongMd, 1, "ja", W1);

        var rightRecord = await _kit.Db.AddRecordAsync("ccos2", "Synthetic Other One-shot");
        var right = await _kit.AddBookAsync(null, "Synthetic Other One-shot", 800, 1200, L2);
        await LinkAsync(right, rightRecord, SeriesLinkState.Auto);
        var rightMd = await _kit.AddCompanionAsync(rightRecord);
        await _kit.AddStoredCoverAsync(rightMd, null, "ja", Near(L2, 2), VolumeCoverKind.Main);

        Assert.Equal(new CoverCheckSweepResult(2, 1), await Service().SweepAsync());

        Assert.Equal((int)SeriesLinkState.NeedsReview, (await LinkOfAsync(wrong.Id)).State);
        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(right.Id)).State);
    }

    [Fact]
    public async Task ASpreadPage1_IsComparedByItsCropHalves_AndNotAtAllWithoutThem()
    {
        var record = await _kit.Db.AddRecordAsync("ccsp", "Synthetic Spreads");
        var folder = await _kit.Db.AddFolderAsync(null, "Synthetic Spreads");
        var v1 = await _kit.AddBookAsync(folder, "Synthetic Spreads v01", 1600, 1200, L1);
        var v2 = await _kit.AddBookAsync(folder, "Synthetic Spreads v02", 1600, 1200, L2);
        await LinkAsync(folder, record, SeriesLinkState.Auto);
        var md = await _kit.AddCompanionAsync(record);
        await _kit.AddStoredCoverAsync(md, 1, "en", W1);
        await _kit.AddStoredCoverAsync(md, 2, "en", W2);

        // No crops on disk: a whole jacket is not compared with a single cover - nothing happens.
        Assert.Equal(new CoverCheckSweepResult(0, 0), await Service().SweepAsync());
        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(folder.Id)).State);

        // Nothing changed in the database: the next tick does not even read the links.
        Assert.Equal(new CoverCheckSweepResult(0, 0), await Service().SweepAsync());

        // The crop halves appear (the cover layer rendered them): the right half of volume 1 is the web cover. Files are picked up by
        // the periodic full sweep.
        _time.Advance(CoverCheckService.FullSweepEvery);
        await WriteCropAsync(v1.Id, CoverCropSide.Left, L2 ^ 0xFFFF);
        await WriteCropAsync(v1.Id, CoverCropSide.Right, Near(W1, 5));
        await WriteCropAsync(v2.Id, CoverCropSide.Left, L1 ^ 0xFFFF_0000);
        await WriteCropAsync(v2.Id, CoverCropSide.Right, L1 ^ 0xFFFF_0000_0000);

        Assert.Equal(new CoverCheckSweepResult(1, 0), await Service().SweepAsync());
        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(folder.Id)).State);
        Assert.Contains(_logs.Lines, l => l.Contains("Agrees", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnchangedInputs_AreNotHashedAgain_ANewStoredCoverChecksAgain()
    {
        var (folder, _, md) = await SeriesAsync(SeriesLinkState.Auto, L1, L2, L1 ^ 0xFFFF_0000_0000_0000);
        await _kit.AddStoredCoverAsync(md, 1, "en", W1);
        await _kit.AddStoredCoverAsync(md, 2, "en", Near(L2, 15)); // in between: unsure

        Assert.Equal(new CoverCheckSweepResult(1, 0), await Service().SweepAsync());
        var calls = _kit.Hasher.Calls;
        Assert.Equal(new CoverCheckSweepResult(0, 0), await Service().SweepAsync());
        Assert.Equal(calls, _kit.Hasher.Calls);

        // Volume 3's cover arrives: checked again (still unsure - volume 2 is in between - so the link stays).
        await _kit.AddStoredCoverAsync(md, 3, "en", W2);
        Assert.Equal(new CoverCheckSweepResult(1, 0), await Service().SweepAsync());
        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(folder.Id)).State);
    }

    [Fact]
    public async Task CompareCoversOff_ChecksNothing()
    {
        var (folder, _, md) = await SeriesAsync(SeriesLinkState.Auto, L1, L2);
        await _kit.AddStoredCoverAsync(md, 1, "en", W1);
        await _kit.AddStoredCoverAsync(md, 2, "en", W2);
        (await _kit.SettingsAsync()).MetadataCoverCompareEnabled = false;
        await _kit.Db.Db.SaveChangesAsync();

        Assert.Equal(new CoverCheckSweepResult(0, 0), await Service().SweepAsync());
        Assert.Null(await Service().CheckNodeAsync(folder.Id));
        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(folder.Id)).State);
    }

    [Fact]
    public async Task AChapterFolder_IsNeverChecked()
    {
        var record = await _kit.Db.AddRecordAsync("ccch", "Synthetic Chapters");
        var folder = await _kit.Db.AddFolderAsync(null, "Synthetic Chapters");
        for (var c = 1; c <= 4; c++)
            await _kit.AddBookAsync(folder, $"Synthetic Chapters c{c:000}", 800, 1200, c % 2 == 0 ? L1 : L2);
        await LinkAsync(folder, record, SeriesLinkState.Auto);
        var md = await _kit.AddCompanionAsync(record);
        await _kit.AddStoredCoverAsync(md, 1, "en", W1);

        Assert.Equal(new CoverCheckSweepResult(0, 0), await Service().SweepAsync());
        Assert.Equal(0, _kit.Hasher.Calls);
    }

    private async Task WriteCropAsync(long nodeId, CoverCropSide side, ulong hash)
    {
        var path = _kit.Files.CropPath(nodeId, 1, side);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, CoverLayerTestKit.HashFile(hash));
    }
}
