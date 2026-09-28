namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

/// <summary>Scheduling options of the automatic-matching worker.</summary>
public sealed record MetadataAutoMatchOptions
{
    /// <summary>Master switch of the worker loop (tests turn it off and drive passes directly).</summary>
    public bool WorkerEnabled { get; init; } = true;

    /// <summary>Delay after boot before the first pass (startup recovery settles first).</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The worker wakes at least this often (and immediately on a signal).</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The id-only refresh pass runs at most this often.</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// The provider-author half of the artist-folder rule (1.28.0): a collection-shaped leaf named like the
    /// author of a record linked in the library is an artist folder. Off = the 1.27.0 detector (the before /
    /// after switch of the counts-only replay).
    /// </summary>
    public bool ProviderAuthorFolders { get; init; } = true;

    /// <summary>
    /// Kill switch of the cover comparison (1.28.0): false = never download a candidate cover, whatever the
    /// "Compare covers" setting says (<c>Metadata:AutoMatch:CompareCovers</c>).
    /// </summary>
    public bool CompareCovers { get; init; } = true;

    /// <summary>
    /// Reads <c>Metadata:AutoMatch:WorkerEnabled</c> / <c>StartupDelaySeconds</c> / <c>TickSeconds</c> /
    /// <c>ProviderAuthorFolders</c> / <c>CompareCovers</c>.
    /// </summary>
    public static MetadataAutoMatchOptions FromConfiguration(IConfiguration config)
    {
        var section = config.GetSection("Metadata:AutoMatch");
        var defaults = new MetadataAutoMatchOptions();
        return new MetadataAutoMatchOptions
        {
            WorkerEnabled = bool.TryParse(section["WorkerEnabled"], out var enabled) ? enabled : defaults.WorkerEnabled,
            StartupDelay = int.TryParse(section["StartupDelaySeconds"], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var delay) && delay >= 0 ? TimeSpan.FromSeconds(delay) : defaults.StartupDelay,
            TickInterval = int.TryParse(section["TickSeconds"], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var tick) && tick >= 1 ? TimeSpan.FromSeconds(tick) : defaults.TickInterval,
            ProviderAuthorFolders = bool.TryParse(section["ProviderAuthorFolders"], out var authors) ? authors : defaults.ProviderAuthorFolders,
            CompareCovers = bool.TryParse(section["CompareCovers"], out var covers) ? covers : defaults.CompareCovers,
        };
    }
}

/// <summary>
/// Process-wide automatic-matching state (singleton): the wake-up signal, the last
/// wait reason (for the runs endpoint), and a per-library tree snapshot cached by
/// catalog revision so the worker does not reload the tree for every work.
/// </summary>
public sealed class MetadataAutoMatchState
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _gate = new();
    private LibraryTreeSnapshot? _snapshot;

    public string? WaitingCode { get; private set; }
    public DateTimeOffset? WaitingUntil { get; private set; }
    public bool Working { get; private set; }

    /// <summary>Wakes the worker (idempotent while a wake-up is pending).</summary>
    public void Signal()
    {
        lock (_gate)
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await _signal.WaitAsync(timeout, ct);

    public void SetStatus(string? waitingCode, DateTimeOffset? until, bool working)
    {
        WaitingCode = waitingCode;
        WaitingUntil = until;
        Working = working;
    }

    public LibraryTreeSnapshot? CachedSnapshot(long libraryId, long revision)
    {
        lock (_gate)
            return _snapshot is { } s && s.LibraryId == libraryId && s.CatalogRevision == revision ? s : null;
    }

    public void Cache(LibraryTreeSnapshot snapshot)
    {
        lock (_gate)
            _snapshot = snapshot;
    }
}
