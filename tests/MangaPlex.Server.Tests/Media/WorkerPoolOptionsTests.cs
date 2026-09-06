namespace com.lifepixer.mangaplex.Tests.Server.Media;

using com.lifepixer.mangaplex.Server.Media;
using Xunit;

/// <summary>
/// Tests for WorkerPoolOptions defaults and configuration.
/// Verifies storage-aware timeout defaults for Unraid cold-drive deployments.
/// </summary>
public sealed class WorkerPoolOptionsTests
{
    [Fact]
    public void Defaults_MatchConfirmedP07Decisions()
    {
        var options = new WorkerPoolOptions();

        Assert.Equal(2, options.MaxConcurrentJobs);
        Assert.Equal(TimeSpan.FromSeconds(15), options.StartupHandshakeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), options.SourceOpenTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.QuickProbeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), options.AnalysisTimeout);
        Assert.Equal(TimeSpan.FromSeconds(300), options.SolidPreparationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), options.CancellationGracePeriod);
        Assert.Equal(5, options.MaxConsecutiveStartupFailures);
    }

    [Fact]
    public void Defaults_ScratchBudgetIs2GiB()
    {
        var options = new WorkerPoolOptions();
        Assert.Equal(2L * 1024 * 1024 * 1024, options.ScratchBudgetBytes);
    }

    [Fact]
    public void Defaults_CacheBudgetIs10GiB()
    {
        var options = new WorkerPoolOptions();
        Assert.Equal(10L * 1024 * 1024 * 1024, options.CacheBudgetBytes);
    }

    [Fact]
    public void JobPriority_CurrentPageIsHighest()
    {
        Assert.True(JobPriority.CurrentPage > JobPriority.Prefetch);
        Assert.True(JobPriority.Prefetch > JobPriority.Background);
    }

    [Fact]
    public void JobOperation_DefinesAllExpectedOperations()
    {
        Assert.Equal(0, (int)JobOperation.Analyze);
        Assert.Equal(1, (int)JobOperation.ExtractPage);
        Assert.Equal(2, (int)JobOperation.GenerateCover);
        Assert.Equal(3, (int)JobOperation.PrepareSolid);
    }
}
