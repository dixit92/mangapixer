namespace com.lifepixer.mangapixer.Server.Features.Ratings;

/// <summary>
/// The rating-change seam (1.53.0 step 0, for lane T's tracker scores): <see cref="RatingsService"/> calls every registered sink once
/// AFTER a user's rating of a node was saved - set the first time, changed, or cleared - and never when nothing changed (the same score
/// again, or a clear with no rating there). Sinks run in the request, one after the other; a sink that throws does not fail the request
/// (the service logs it at Warning, <c>LogEvents.Ratings.RatingSinkFailed</c>, and calls the next one), so a sink that does slow work
/// should hand it off rather than do it inline. No sink is registered by default: register one with
/// <c>services.AddScoped&lt;IRatingChangeSink, ...&gt;()</c> (resolved as <c>IEnumerable&lt;IRatingChangeSink&gt;</c>).
/// </summary>
public interface IRatingChangeSink
{
    /// <summary>
    /// The user's rating of the node is now <paramref name="score"/> (1-10), or none when <paramref name="score"/> is null (cleared).
    /// </summary>
    /// <param name="userId">The rater's internal user id.</param>
    /// <param name="catalogNodeId">The internal id of the rated catalog node (a folder or an archive).</param>
    /// <param name="score">The stored score (1-10), or null once the rating was cleared.</param>
    /// <param name="ct">The request's cancellation token.</param>
    Task RatingChangedAsync(long userId, long catalogNodeId, int? score, CancellationToken ct);
}
