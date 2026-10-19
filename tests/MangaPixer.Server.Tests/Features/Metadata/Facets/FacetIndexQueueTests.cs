namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Facets;

using com.lifepixer.mangapixer.Server.Features.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Index;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Xunit;

/// <summary>
/// Unit tests of the tag index's dirty-library queue (1.45.0 lane F): a nudge only marks; a library is due once its LAST nudge is a
/// debounce old (more nudges push it back); an "every library" nudge is due on the same rule; a requeued library waits a debounce again.
/// </summary>
public sealed class FacetIndexQueueTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(30);

    [Fact]
    public void ALibrary_IsDueOneDebounceAfterItsLastNudge()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero));
        var queue = new FacetIndexQueue(time);

        queue.LibraryChanged(1, FacetNudgeReason.LinkChanged);
        time.Advance(TimeSpan.FromSeconds(20));
        queue.LibraryChanged(1, FacetNudgeReason.RecordStored);
        queue.LibraryChanged(2, FacetNudgeReason.ScanFinished);
        time.Advance(TimeSpan.FromSeconds(20));

        var (early, all) = queue.TakeDue(Debounce, out var next);
        Assert.Empty(early);
        Assert.False(all);
        Assert.Equal(time.Now.AddSeconds(10), next);
        Assert.Equal(2, queue.Pending);

        time.Advance(TimeSpan.FromSeconds(10));
        var (due, _) = queue.TakeDue(Debounce, out next);
        Assert.Equal(new long[] { 1, 2 }, due.Order().ToArray());
        Assert.Null(next);
        Assert.Equal(0, queue.Pending);

        queue.Requeue(2);
        Assert.Empty(queue.TakeDue(Debounce, out _).Libraries);
        time.Advance(Debounce);
        Assert.Equal(new long[] { 2 }, queue.TakeDue(Debounce, out _).Libraries.ToArray());
    }

    [Fact]
    public void AnEveryLibraryNudge_IsDueOnceAfterTheDebounce()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero));
        var queue = new FacetIndexQueue(time);

        queue.AllChanged(FacetNudgeReason.AuthorAliasesStored);
        Assert.False(queue.TakeDue(Debounce, out var next).All);
        Assert.Equal(time.Now + Debounce, next);
        time.Advance(Debounce);
        Assert.True(queue.TakeDue(Debounce, out _).All);
        Assert.False(queue.TakeDue(Debounce, out _).All);
    }
}
