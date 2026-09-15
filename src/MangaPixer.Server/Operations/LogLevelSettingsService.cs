namespace com.lifepixer.mangapixer.Server.Operations;

using com.lifepixer.mangapixer.Server.Logging;

using Serilog.Core;
using Serilog.Events;

/// <summary>
/// Manages the runtime Serilog minimum level via a <see cref="LoggingLevelSwitch"/>.
/// The switch is ephemeral - it resets to the <c>appsettings.json</c> default
/// (Information) on restart. In addition to the global default level, individual
/// <see cref="DebugCategories"/> can be overridden to a different level so an
/// admin can enable Debug for ONE subsystem (e.g. Scanning) without the whole
/// firehose. Per-category overrides default to "inherit" (they follow the
/// global switch); setting a category to an explicit level detaches it from
/// the global switch until it is cleared back to inherit. The
/// <c>Microsoft.*</c> overrides remain hardcoded at Warning.
/// </summary>
public sealed class LogLevelSettingsService
{
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly IReadOnlyDictionary<string, LoggingLevelSwitch> _categorySwitches;
    private readonly HashSet<string> _explicitCategories = new();
    private readonly ILogger<LogLevelSettingsService> _logger;

    public LogLevelSettingsService(
        LoggingLevelSwitch levelSwitch,
        IReadOnlyDictionary<string, LoggingLevelSwitch> categorySwitches,
        ILogger<LogLevelSettingsService> logger)
    {
        _levelSwitch = levelSwitch;
        _categorySwitches = categorySwitches;
        _logger = logger;
    }

    /// <summary>
    /// Returns the current global minimum log level.
    /// </summary>
    public LogEventLevel GetCurrent() => _levelSwitch.MinimumLevel;

    /// <summary>
    /// Sets the global minimum log level live (no restart). Also syncs every
    /// per-category switch that is still in "inherit" mode to the new level so
    /// they continue tracking the global default. Logs the change at
    /// Information so the audit trail captures who changed verbosity.
    /// </summary>
    public void SetLevel(LogEventLevel level, string userName)
    {
        _levelSwitch.MinimumLevel = level;

        // Sync all inherit-mode category switches to the new global level.
        foreach (var (name, sw) in _categorySwitches)
        {
            if (!_explicitCategories.Contains(name))
                sw.MinimumLevel = level;
        }

        _logger.LogInformation(LogEvents.Administration.LogLevelChanged, "Log level changed to {Level} by {UserName}", level, userName);
    }

    /// <summary>
    /// Returns the current level and inherit state for every debug category.
    /// </summary>
    public IReadOnlyList<DebugCategoryLevel> GetCategoryLevels()
    {
        var result = new List<DebugCategoryLevel>(_categorySwitches.Count);
        foreach (var (name, sw) in _categorySwitches)
        {
            result.Add(new DebugCategoryLevel(
                name,
                sw.MinimumLevel,
                !_explicitCategories.Contains(name)));
        }
        return result;
    }

    /// <summary>
    /// Sets a per-category minimum level live (no restart). The category
    /// detaches from the global switch and stays at this level until cleared.
    /// Logs the change at Information for the audit trail.
    /// </summary>
    public void SetCategoryLevel(string name, LogEventLevel level, string userName)
    {
        if (!_categorySwitches.TryGetValue(name, out var sw))
            throw new ArgumentException($"Unknown debug category: {name}", nameof(name));

        sw.MinimumLevel = level;
        _explicitCategories.Add(name);
        _logger.LogInformation(LogEvents.Administration.LogCategoryLevelChanged,
            "Log category {Category} level changed to {Level} by {UserName}", name, level, userName);
    }

    /// <summary>
    /// Clears a per-category override so it returns to following the global
    /// switch. Logs the change at Information for the audit trail.
    /// </summary>
    public void ClearCategoryLevel(string name, string userName)
    {
        if (!_categorySwitches.TryGetValue(name, out var sw))
            throw new ArgumentException($"Unknown debug category: {name}", nameof(name));

        _explicitCategories.Remove(name);
        sw.MinimumLevel = _levelSwitch.MinimumLevel;
        _logger.LogInformation(LogEvents.Administration.LogCategoryLevelCleared,
            "Log category {Category} override cleared by {UserName}", name, userName);
    }
}

/// <summary>
/// A debug category's current level and whether it is inheriting the global
/// switch or has an explicit override.
/// </summary>
public sealed record DebugCategoryLevel(string Name, LogEventLevel Level, bool Inherited);
