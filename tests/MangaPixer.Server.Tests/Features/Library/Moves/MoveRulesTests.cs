namespace com.lifepixer.mangapixer.Tests.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Library.Moves;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>Unit tests of the pure move rules (1.31.0): pairing, page agreement, per-user state and links.</summary>
public sealed class MoveRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static MoveOldCandidate Old(long id, string sig, long size = 100) => new(id, 1, size, sig, T0);
    private static MoveNewCandidate New(long id, string? sig, long size = 100, int minutes = 10) => new(id, 2, size, sig, T0.AddMinutes(minutes));

    [Fact]
    public void Pairing_OneOldOneNew_Pairs()
    {
        var d = Assert.Single(MovePairing.Decide([Old(1, "a")], [New(2, "a")]));
        Assert.Equal(MovePairOutcome.Paired, d.Outcome);
        Assert.Equal(2, d.New!.NodeId);
    }

    [Fact]
    public void Pairing_TwoOldWithOneSignature_IsAmbiguous() =>
        Assert.All(MovePairing.Decide([Old(1, "a"), Old(3, "a")], [New(2, "a")]), d => Assert.Equal(MovePairOutcome.Ambiguous, d.Outcome));

    [Fact]
    public void Pairing_TwoEligibleNewCopies_IsAmbiguous() =>
        Assert.Equal(MovePairOutcome.Ambiguous, MovePairing.Decide([Old(1, "a")], [New(2, "a"), New(3, "a")])[0].Outcome);

    [Fact]
    public void Pairing_ACopyThatExistedWhileTheOldWasSeen_StaysSeparate()
    {
        Assert.Equal(MovePairOutcome.NoPartner, MovePairing.Decide([Old(1, "a")], [New(2, "a", minutes: -5)])[0].Outcome);
        // ...and does not make a later copy ambiguous.
        Assert.Equal(MovePairOutcome.Paired, MovePairing.Decide([Old(1, "a")], [New(2, "a", minutes: -5), New(3, "a")])[0].Outcome);
    }

    [Fact]
    public void Pairing_ASameSizeCopyWaitingForAnalysis_Waits()
    {
        Assert.Equal(MovePairOutcome.Waiting, MovePairing.Decide([Old(1, "a")], [New(2, "a"), New(3, null)])[0].Outcome);
        Assert.Equal(MovePairOutcome.Paired, MovePairing.Decide([Old(1, "a")], [New(2, "a"), New(3, null, size: 99)])[0].Outcome);
        Assert.Equal(MovePairOutcome.Paired, MovePairing.Decide([Old(1, "a")], [New(2, "a"), New(3, null, minutes: -5)])[0].Outcome);
    }

    [Fact]
    public void Pairing_OtherSignatureOrSize_IsNoPartner()
    {
        Assert.Equal(MovePairOutcome.NoPartner, MovePairing.Decide([Old(1, "a")], [New(2, "b")])[0].Outcome);
        Assert.Equal(MovePairOutcome.NoPartner, MovePairing.Decide([Old(1, "a")], [New(2, "a", size: 101)])[0].Outcome);
    }

    private static ManifestPage P(int ordinal, string locator, long size) => new(ordinal, "k" + ordinal, locator, size);

    [Fact]
    public void Manifest_SameEntriesInAnotherOrder_Agree_AndMapByEntry()
    {
        var old = new[] { P(0, "a.jpg", 10), P(1, "b.jpg", 20), P(2, "c.jpg", 30) };
        var neu = new[] { P(0, "c.jpg", 30), P(1, "a.jpg", 10), P(2, "b.jpg", 20) };
        Assert.True(ManifestAgreement.Agree(old, neu));
        Assert.False(ManifestAgreement.SameOrder(old, neu));
        var map = ManifestAgreement.MapByEntry(old, neu);
        Assert.Equal(1, map["k0"].Ordinal);
        Assert.Equal(0, map["k2"].Ordinal);
    }

    [Fact]
    public void Manifest_CountNamesOrSizesDiffer_Disagree()
    {
        var old = new[] { P(0, "a.jpg", 10), P(1, "b.jpg", 20) };
        Assert.False(ManifestAgreement.Agree(old, [P(0, "a.jpg", 10)]));
        Assert.False(ManifestAgreement.Agree(old, [P(0, "a.jpg", 10), P(1, "x.jpg", 20)]));
        Assert.False(ManifestAgreement.Agree(old, [P(0, "a.jpg", 10), P(1, "b.jpg", 21)]));
        Assert.False(ManifestAgreement.Agree([], []));
        Assert.True(ManifestAgreement.SameOrder(old, [P(0, "a.jpg", 10), P(1, "b.jpg", 20)]));
    }

    [Fact]
    public void State_EmptyAndEqualProgress()
    {
        Assert.True(MoveStateRules.IsEmptyProgress(0, 0));
        Assert.False(MoveStateRules.IsEmptyProgress(1, 0));
        Assert.False(MoveStateRules.IsEmptyProgress(0, 3));
        Assert.True(MoveStateRules.SameProgress(1, 4, 1, 4));
        Assert.False(MoveStateRules.SameProgress(1, 4, 1, 5));
        Assert.True(MoveStateRules.SameProgress(2, 9, 2, 3)); // both completed
        Assert.False(MoveStateRules.SameProgress(2, 9, 1, 9));
    }

    private static MoveLinkSnapshot L(SeriesLinkState state, long? record) => new(state, record);

    [Theory]
    [InlineData(null, null, null, null, MoveLinkOutcome.Nothing)]
    [InlineData(SeriesLinkState.Confirmed, 1L, null, null, MoveLinkOutcome.MoveOld)]
    [InlineData(SeriesLinkState.DontMatch, null, null, null, MoveLinkOutcome.MoveOld)]
    [InlineData(SeriesLinkState.NeedsReview, 1L, null, null, MoveLinkOutcome.DropOld)]
    [InlineData(SeriesLinkState.Confirmed, 1L, SeriesLinkState.NeedsReview, 2L, MoveLinkOutcome.ReplaceNewWithOld)]
    [InlineData(SeriesLinkState.DontMatch, null, SeriesLinkState.NeedsReview, 2L, MoveLinkOutcome.ReplaceNewWithOld)]
    [InlineData(SeriesLinkState.Confirmed, 1L, SeriesLinkState.Auto, 1L, MoveLinkOutcome.PromoteNew)]
    [InlineData(SeriesLinkState.Confirmed, 1L, SeriesLinkState.Confirmed, 1L, MoveLinkOutcome.DropOld)]
    [InlineData(SeriesLinkState.Auto, 1L, SeriesLinkState.Auto, 2L, MoveLinkOutcome.DropOld)]
    [InlineData(SeriesLinkState.Auto, 1L, SeriesLinkState.NeedsReview, 2L, MoveLinkOutcome.DropOld)]
    [InlineData(SeriesLinkState.Confirmed, 1L, SeriesLinkState.Auto, 2L, MoveLinkOutcome.Conflict)]
    [InlineData(SeriesLinkState.Confirmed, 1L, SeriesLinkState.Confirmed, 2L, MoveLinkOutcome.Conflict)]
    [InlineData(SeriesLinkState.Confirmed, 1L, SeriesLinkState.DontMatch, null, MoveLinkOutcome.Conflict)]
    [InlineData(SeriesLinkState.DontMatch, null, SeriesLinkState.Auto, 2L, MoveLinkOutcome.Conflict)]
    public void LinkRules(SeriesLinkState? oldState, long? oldRecord, SeriesLinkState? newState, long? newRecord, MoveLinkOutcome expected) =>
        Assert.Equal(expected, MoveLinkRules.Decide(
            oldState is { } o ? L(o, oldRecord) : null,
            newState is { } n ? L(n, newRecord) : null));

    /// <summary>
    /// Every table that refers to a catalog node or item is classified for a move recognised after the fact (and for the
    /// trash, which must know what a node owns). A new table with a node / item id fails here until it is added: copied by the
    /// move rules, regenerated for the new copy, or history that stays with the old node.
    /// </summary>
    [Fact]
    public void EveryNodeKeyedTable_IsClassifiedForMoves()
    {
        var classified = new Dictionary<string, string>
        {
            // Copied onto the new copy (MovePairingService / MetadataCarryOverService).
            ["reading_progress"] = "copied",
            ["read_marks"] = "copied",
            ["bookmarks"] = "copied",
            ["item_reader_overrides"] = "copied",
            ["favorites"] = "copied",
            ["node_series_links"] = "copied",
            ["declared_facts"] = "copied",
            ["node_cover_choices"] = "copied",
            ["archive_spread_layouts"] = "copied",
            ["folder_reader_defaults"] = "copied",
            ["folder_metadata_precedence"] = "copied",
            ["folder_metadata_content"] = "copied",
            ["folder_view_settings"] = "copied",
            ["folder_cover_preferences"] = "copied",
            // The new copy has (or derives) its own.
            ["archive_items"] = "regenerated",
            ["page_entries"] = "regenerated",
            ["embedded_metadata"] = "regenerated",
            ["node_auto_covers"] = "regenerated",
            ["metadata_match_queue"] = "regenerated",
            ["metadata_match_candidates"] = "regenerated",
            // History / bookkeeping that stays where it happened.
            ["catalog_nodes"] = "the node itself",
            ["jobs"] = "history",
            ["scan_observations"] = "history",
            ["audit_events"] = "history",
            ["metadata_flags"] = "history",
            ["node_moves"] = "move bookkeeping",
            // 1.33.0 metadata export: rebuilt from the links; a vanished node's row stays until the rebuild pools its removal.
            ["export_items"] = "export snapshot",
            ["export_carries"] = "export bookkeeping",
        };
        using var db = new MangaPixerDbContext(new DbContextOptionsBuilder<MangaPixerDbContext>().UseSqlite("Data Source=:memory:").Options);
        var nodeKeyed = db.Model.GetEntityTypes()
            .Where(t => t.GetProperties().Any(p => p.ClrType == typeof(long) || p.ClrType == typeof(long?))
                && t.GetProperties().Any(p => p.Name.EndsWith("NodeId", StringComparison.Ordinal) || p.Name.EndsWith("ItemId", StringComparison.Ordinal)))
            .Select(t => t.GetTableName()!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.Contains("reading_progress", nodeKeyed);
        Assert.Contains("node_moves", nodeKeyed);
        Assert.True(nodeKeyed.Count >= 20, string.Join(", ", nodeKeyed));
        var missing = nodeKeyed.Where(t => !classified.ContainsKey(t)).ToList();
        Assert.True(missing.Count == 0, "Classify for moves (and the trash): " + string.Join(", ", missing));
    }
}
