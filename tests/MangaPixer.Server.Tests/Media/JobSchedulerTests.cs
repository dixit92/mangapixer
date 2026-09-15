namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Server.Media;
using Xunit;

/// <summary>
/// Tests for the JobScheduler with deduplication and priority ordering.
/// </summary>
public sealed class JobSchedulerTests
{
    private static JobScheduler CreateScheduler(int maxJobs = 2)
    {
        return new JobScheduler(new WorkerPoolOptions { MaxConcurrentJobs = maxJobs });
    }

    [Fact]
    public void Dequeue_ReturnsNullWhenNoJobsPending()
    {
        var scheduler = CreateScheduler();
        Assert.Null(scheduler.Dequeue());
    }

    [Fact]
    public void Dequeue_ReturnsHighestPriorityJobFirst()
    {
        var scheduler = CreateScheduler();

        // Enqueue in reverse priority order
        scheduler.EnqueueAsync(1, 1, JobOperation.Analyze, JobPriority.Background,
            "/source/a.cbz", 0, 100);
        scheduler.EnqueueAsync(2, 1, JobOperation.Analyze, JobPriority.Prefetch,
            "/source/b.cbz", 0, 100);
        scheduler.EnqueueAsync(3, 1, JobOperation.Analyze, JobPriority.CurrentPage,
            "/source/c.cbz", 0, 100);

        var first = scheduler.Dequeue();
        var second = scheduler.Dequeue();
        var third = scheduler.Dequeue();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);

        Assert.Equal(JobPriority.CurrentPage, first!.Priority);
        Assert.Equal(JobPriority.Prefetch, second!.Priority);
        Assert.Equal(JobPriority.Background, third!.Priority);
    }

    [Fact]
    public async Task Enqueue_DeduplicatesIdenticalJobs()
    {
        var scheduler = CreateScheduler();

        // Same itemId, contentVersion, operation → deduplicated
        var task1 = scheduler.EnqueueAsync(1, 1, JobOperation.Analyze, JobPriority.Background,
            "/source/a.cbz", 0, 100);
        var task2 = scheduler.EnqueueAsync(1, 1, JobOperation.Analyze, JobPriority.Background,
            "/source/a.cbz", 0, 100);

        // Both tasks should share the same completion source
        Assert.Equal(1, scheduler.PendingCount);

        // Complete the job
        var job = scheduler.Dequeue();
        Assert.NotNull(job);
        scheduler.MarkInFlight(job!);
        scheduler.CompleteJob(job!.DedupKey, new JobResult
        {
            JobId = job.JobId,
            Success = true,
            ErrorType = null,
            ErrorMessage = null,
            Result = null,
        });

        // Both tasks should complete with the same result
        var r1 = await task1;
        var r2 = await task2;
        Assert.True(r1.Success);
        Assert.True(r2.Success);
    }

    [Fact]
    public void Enqueue_UpgradesPriorityForHigherPriorityRequest()
    {
        var scheduler = CreateScheduler();

        // Enqueue as background
        scheduler.EnqueueAsync(1, 1, JobOperation.Analyze, JobPriority.Background,
            "/source/a.cbz", 0, 100);

        // Same job requested with higher priority
        scheduler.EnqueueAsync(1, 1, JobOperation.Analyze, JobPriority.CurrentPage,
            "/source/a.cbz", 0, 100);

        var job = scheduler.Dequeue();
        Assert.NotNull(job);
        Assert.Equal(JobPriority.CurrentPage, job!.Priority);
    }

    [Fact]
    public void CancelPendingForItem_RemovesOnlyMatchingJobs()
    {
        var scheduler = CreateScheduler();

        scheduler.EnqueueAsync(1, 1, JobOperation.Analyze, JobPriority.Background,
            "/source/a.cbz", 0, 100);
        scheduler.EnqueueAsync(2, 1, JobOperation.Analyze, JobPriority.Background,
            "/source/b.cbz", 0, 100);
        scheduler.EnqueueAsync(1, 1, JobOperation.ExtractPage, JobPriority.CurrentPage,
            "/source/a.cbz", 0, 100);

        scheduler.CancelPendingForItem(1);

        // Only item 2's job should remain
        var job = scheduler.Dequeue();
        Assert.NotNull(job);
        Assert.Equal(2, job!.ItemId);
    }

    [Fact]
    public async Task MarkInFlight_AndCompleteJob_NotifiesCallers()
    {
        var scheduler = CreateScheduler();

        var task = scheduler.EnqueueAsync(1, 1, JobOperation.Analyze, JobPriority.Background,
            "/source/a.cbz", 0, 100);

        var job = scheduler.Dequeue();
        Assert.NotNull(job);
        scheduler.MarkInFlight(job!);

        Assert.Equal(1, scheduler.InFlightCount);

        scheduler.CompleteJob(job!.DedupKey, new JobResult
        {
            JobId = job.JobId,
            Success = true,
            ErrorType = null,
            ErrorMessage = null,
            Result = "test-result",
        });

        Assert.Equal(0, scheduler.InFlightCount);
        var result = await task;
        Assert.True(result.Success);
    }

    [Fact]
    public async Task FailJob_NotifiesCallersWithError()
    {
        var scheduler = CreateScheduler();

        var task = scheduler.EnqueueAsync(1, 1, JobOperation.Analyze, JobPriority.Background,
            "/source/a.cbz", 0, 100);

        var job = scheduler.Dequeue();
        scheduler.MarkInFlight(job!);

        scheduler.FailJob(job!.DedupKey, new InvalidOperationException("worker crashed"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
    }

    [Fact]
    public void Dequeue_FIFOWithinSamePriority()
    {
        var scheduler = CreateScheduler();

        scheduler.EnqueueAsync(1, 1, JobOperation.Analyze, JobPriority.Background,
            "/source/a.cbz", 0, 100);
        scheduler.EnqueueAsync(2, 1, JobOperation.Analyze, JobPriority.Background,
            "/source/b.cbz", 0, 100);
        scheduler.EnqueueAsync(3, 1, JobOperation.Analyze, JobPriority.Background,
            "/source/c.cbz", 0, 100);

        var first = scheduler.Dequeue();
        var second = scheduler.Dequeue();
        var third = scheduler.Dequeue();

        Assert.Equal(1, first!.ItemId);
        Assert.Equal(2, second!.ItemId);
        Assert.Equal(3, third!.ItemId);
    }
}
