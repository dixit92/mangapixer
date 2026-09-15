namespace com.lifepixer.mangaplex.Tray.Settings;

/// <summary>
/// Persisted tray preferences. Deliberately tiny: "start at sign-in" is not
/// stored here because HKCU Run is itself the source of truth for that
/// checkbox (see <see cref="Startup.RunKeyService"/>) — duplicating it here
/// would let the two drift if the Run key is edited outside the tray.
/// </summary>
public sealed class TraySettings
{
    /// <summary>
    /// Bump when the schema shape changes. <see cref="TraySettingsStore"/>
    /// falls back to defaults for a file it cannot deserialize (older tray
    /// versions never wrote unknown fields, so forward-compat round-tripping
    /// is not needed yet).
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// When true, the server is relaunched bound to 0.0.0.0 instead of the
    /// loopback-only default. Applied on the next server start/restart, not
    /// live — see the cycle-context note in the lane prompt.
    /// </summary>
    public bool AllowLanAccess { get; set; }
}
