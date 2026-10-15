namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using com.lifepixer.mangapixer.Core.Trackers;

/// <summary>
/// The registered <see cref="ITrackerClient"/>s by slug (the adapters register them in <c>AddTrackerAdapters</c>; tests register a
/// fake). A tracker without a client is "unavailable": nothing is sent to it.
/// </summary>
public sealed class TrackerClients(IEnumerable<ITrackerClient> clients)
{
    private readonly Dictionary<string, ITrackerClient> _bySlug = clients
        .Where(c => TrackerFacts.IsKnown(c.Slug))
        .GroupBy(c => c.Slug, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

    public ITrackerClient? Get(string tracker) => _bySlug.GetValueOrDefault(tracker);
}
