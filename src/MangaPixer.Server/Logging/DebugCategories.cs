namespace com.lifepixer.mangaplex.Server.Logging;

/// <summary>
/// The fixed catalog of per-subsystem debug categories. Each entry maps a
/// human-readable <see cref="Name"/> (surfaced to admins) to a Serilog
/// SourceContext prefix. A <see cref="Serilog.Core.LoggingLevelSwitch"/> is
/// created per prefix and wired via <c>MinimumLevel.Override</c> so an admin
/// can raise or lower the Debug verbosity of ONE subsystem independently of
/// the global switch.
///
/// Prefixes are disjoint (no prefix is a prefix of another) to avoid Serilog
/// override precedence ambiguity. Categories are chosen to cover the
/// high-volume per-item / per-node / per-request <c>LogDebug</c> sites that
/// dominate the firehose during a large-library scan or active reading.
/// </summary>
public static class DebugCategories
{
    /// <summary>
    /// All debug categories, ordered. Each maps to a Serilog SourceContext
    /// prefix (the .NET namespace of the subsystem's loggers).
    /// </summary>
    public static readonly (string Name, string SourceContextPrefix)[] All =
    [
        // Scan-phase timings, per-node move detection, reconciliation, tombstoning.
        // High volume during a large-library scan (per-node LogDebug).
        ("Scanning", "com.lifepixer.mangaplex.Server.Scanning"),

        // Worker pool dispatch, scheduler enqueue/complete, supervisor, cache,
        // thumbnails, page delivery, scratch, analysis persistence. High volume
        // during scan-triggered analysis and active reading (per-job / per-item /
        // per-request LogDebug).
        ("Media", "com.lifepixer.mangaplex.Server.Media"),

        // Page controller: per-request cache hit/miss and thumbnail serve/miss.
        // Moderate volume during active reading.
        ("Reading", "com.lifepixer.mangaplex.Server.Features.Reading"),
    ];

    /// <summary>
    /// Returns true if <paramref name="name"/> is a known debug category.
    /// </summary>
    public static bool IsValid(string name) =>
        Array.FindIndex(All, c => c.Name == name) >= 0;
}
