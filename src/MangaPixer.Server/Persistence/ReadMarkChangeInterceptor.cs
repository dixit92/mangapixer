namespace com.lifepixer.mangapixer.Server.Persistence;

using System.Runtime.CompilerServices;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

/// <summary>
/// Feeds <see cref="IReadMarkObserver"/> (1.43.0 tracker seam): captures the <c>ReadMarkEntity</c> rows a save inserts or deletes,
/// and hands them to the observer only after the save succeeded. One singleton serves every context; the pending rows are kept per
/// context instance. Registered on the app's DbContext only (the worker, backup / restore and design-time contexts have no observer).
/// </summary>
public sealed class ReadMarkChangeInterceptor : SaveChangesInterceptor
{
    private readonly IReadMarkObserver _observer;
    private readonly ILogger<ReadMarkChangeInterceptor> _logger;
    private readonly ConditionalWeakTable<DbContext, List<ReadMarkChange>> _pending = new();

    public ReadMarkChangeInterceptor(IReadMarkObserver observer, ILogger<ReadMarkChangeInterceptor> logger)
    {
        _observer = observer;
        _logger = logger;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Publish(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Publish(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Discard(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => Discard(eventData.Context);

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
            return;
        List<ReadMarkChange>? changes = null;
        foreach (var entry in context.ChangeTracker.Entries<ReadMarkEntity>())
        {
            var kind = entry.State switch
            {
                EntityState.Added => ReadMarkChangeKind.Added,
                EntityState.Deleted => ReadMarkChangeKind.Removed,
                _ => (ReadMarkChangeKind?)null,
            };
            if (kind is null)
                continue;
            var mark = entry.Entity;
            (changes ??= []).Add(new ReadMarkChange(kind.Value, mark.UserId, mark.ItemId, mark.Source, mark.MarkedAt));
        }
        if (changes is null)
            _pending.Remove(context);
        else
            _pending.AddOrUpdate(context, changes);
    }

    private void Publish(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var changes))
            return;
        _pending.Remove(context);
        try
        {
            _observer.OnReadMarksChanged(changes);
        }
        catch (Exception ex)
        {
            // The marks are saved; a failing observer must not fail the request. Counts only - no ids.
            _logger.LogWarning(LogEvents.Trackers.ReadMarkObserverFailed, ex,
                "Read-mark observer failed for {Count} change(s)", changes.Count);
        }
    }

    private void Discard(DbContext? context)
    {
        if (context is not null)
            _pending.Remove(context);
    }
}
