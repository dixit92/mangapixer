namespace com.lifepixer.mangaplex.Server.Operations;

using Serilog.Core;
using Serilog.Events;

/// <summary>
/// Manages the runtime Serilog minimum level via a <see cref="LoggingLevelSwitch"/>.
/// The switch is ephemeral — it resets to the <c>appsettings.json</c> default
/// (Information) on restart. Only the global default level is adjustable;
/// the <c>Microsoft.*</c> overrides remain hardcoded at Warning.
/// </summary>
public sealed class LogLevelSettingsService
{
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly ILogger<LogLevelSettingsService> _logger;

    public LogLevelSettingsService(LoggingLevelSwitch levelSwitch, ILogger<LogLevelSettingsService> logger)
    {
        _levelSwitch = levelSwitch;
        _logger = logger;
    }

    /// <summary>
    /// Returns the current minimum log level.
    /// </summary>
    public LogEventLevel GetCurrent() => _levelSwitch.MinimumLevel;

    /// <summary>
    /// Sets the minimum log level live (no restart). Logs the change at
    /// Information so the audit trail captures who changed verbosity.
    /// </summary>
    public void SetLevel(LogEventLevel level, string userName)
    {
        _levelSwitch.MinimumLevel = level;
        _logger.LogInformation("Log level changed to {Level} by {UserName}", level, userName);
    }
}
