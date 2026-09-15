namespace com.lifepixer.mangaplex.Server.Hosting;

using Serilog.Core;
using Serilog.Events;

/// <summary>
/// Serilog log event enricher that redacts properties whose names suggest
/// sensitive or path-bearing content. Defense-in-depth: domain code already
/// avoids logging paths, but this prevents a stray property from leaking an
/// absolute source path, password, token, cookie, archive entry name, or title
/// into structured logs.
/// </summary>
public sealed class RedactingDestructuringPolicy : ILogEventEnricher
{
    private static readonly string[] SensitiveSuffixes =
    [
        "Path",
        "Password",
        "Token",
        "Cookie",
        "Secret",
        "ApiKey",
        "ConnectionString",
    ];

    private static readonly string[] SensitiveExactNames =
    [
        "EntryName",
        "Title",
        "ArchivePath",
        "RootPath",
        "SourcePath",
        "ScratchPath",
        "CachePath",
        "DataRoot",
    ];

    void ILogEventEnricher.Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var keysToRedact = new List<string>();
        foreach (var kvp in logEvent.Properties)
        {
            if (IsSensitiveName(kvp.Key))
                keysToRedact.Add(kvp.Key);
        }

        foreach (var key in keysToRedact)
        {
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, new ScalarValue("[redacted]")));
        }
    }

    /// <summary>
    /// Returns true if a property name should be redacted.
    /// </summary>
    public static bool IsSensitiveName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var exact in SensitiveExactNames)
            if (string.Equals(name, exact, StringComparison.Ordinal))
                return true;

        foreach (var suffix in SensitiveSuffixes)
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                return true;

        return false;
    }
}
