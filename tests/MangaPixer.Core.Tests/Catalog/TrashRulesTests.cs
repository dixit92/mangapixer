namespace com.lifepixer.mangapixer.Tests.Core.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using Xunit;

/// <summary>Unit tests for the trash's pure rules (1.31.0): library holds and the leaf-first purge plan.</summary>
public sealed class TrashRulesTests
{
    [Theory]
    [InlineData(true, "root_unavailable", 9, 10, TrashHold.ScanRunning)]
    [InlineData(false, "root_unavailable", 1, 10, TrashHold.RootUnavailable)]
    [InlineData(false, null, 6, 10, TrashHold.Burst)]
    [InlineData(false, null, 5, 10, TrashHold.None)]
    [InlineData(false, "IOException", 2, 10, TrashHold.None)]
    [InlineData(false, null, 0, 0, TrashHold.None)]
    [InlineData(false, null, 1, 1, TrashHold.Burst)]
    public void HoldOf_PicksTheMostImportantHold(bool scanRunning, string? error, int eligible, int total, TrashHold expected) =>
        Assert.Equal(expected, TrashRules.HoldOf(scanRunning, error, eligible, total));

    [Fact]
    public void OnlyRootAndBurstHolds_AreReleasable()
    {
        Assert.True(TrashRules.IsReleasable(TrashHold.RootUnavailable));
        Assert.True(TrashRules.IsReleasable(TrashHold.Burst));
        Assert.False(TrashRules.IsReleasable(TrashHold.ScanRunning));
        Assert.False(TrashRules.IsReleasable(TrashHold.None));
        Assert.Equal("burst", TrashRules.CodeOf(TrashHold.Burst));
        Assert.Null(TrashRules.CodeOf(TrashHold.None));
    }

    [Fact]
    public void Plan_DeletesArchivesFirst_ThenFoldersDeepestFirst()
    {
        // 1 (folder) > 2 (folder) > 3 (archive); 1 > 4 (archive)
        var eligible = new[] { new TrashNode(1, null, 0), new TrashNode(2, 1, 0), new TrashNode(3, 2, 1), new TrashNode(4, 1, 1) };
        var children = new[] { new TrashChild(2, 1), new TrashChild(4, 1), new TrashChild(3, 2) };

        var plan = TrashRules.Plan(eligible, children);

        Assert.Equal([3, 4], plan.Archives);
        Assert.Equal(2, plan.FolderRounds.Count);
        Assert.Equal([2], plan.FolderRounds[0]);
        Assert.Equal([1], plan.FolderRounds[1]);
        Assert.Equal(0, plan.KeptFolders);
        Assert.Equal(4, plan.NodeCount);
    }

    [Fact]
    public void Plan_KeepsEveryFolderAboveAKeptChild()
    {
        // 1 > 2 > 3 (archive, eligible) and 2 > 5 (archive, NOT eligible: live or held); 1 > 6 (folder, eligible, empty)
        var eligible = new[] { new TrashNode(1, null, 0), new TrashNode(2, 1, 0), new TrashNode(3, 2, 1), new TrashNode(6, 1, 0) };
        var children = new[] { new TrashChild(2, 1), new TrashChild(6, 1), new TrashChild(3, 2), new TrashChild(5, 2) };

        var plan = TrashRules.Plan(eligible, children);

        Assert.Equal([3], plan.Archives);
        Assert.Single(plan.FolderRounds);
        Assert.Equal([6], plan.FolderRounds[0]);
        Assert.Equal(2, plan.KeptFolders);
        Assert.DoesNotContain(2L, plan.AllNodeIds);
        Assert.DoesNotContain(1L, plan.AllNodeIds);
    }

    [Fact]
    public void Plan_OfNothing_IsEmpty()
    {
        var plan = TrashRules.Plan([], []);
        Assert.Empty(plan.Archives);
        Assert.Empty(plan.FolderRounds);
        Assert.Equal(0, plan.NodeCount);
    }
}
