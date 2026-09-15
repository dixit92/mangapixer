using Microsoft.Win32;

namespace com.lifepixer.mangapixer.Tray.Startup;

/// <summary>
/// Reads/writes the HKCU Run-key entry that starts the tray at sign-in. The
/// registry value itself is the source of truth (no duplicate flag in
/// <c>tray-settings.json</c>) so the checkbox always reflects reality even if
/// the entry is edited outside the tray.
/// </summary>
public sealed class RunKeyService
{
    private const string DefaultSubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _valueName;
    private readonly string _subKeyPath;

    /// <param name="valueName">Run-key value name, e.g. "MangaPlex".</param>
    /// <param name="subKeyPath">
    /// Override for tests only — production always uses the real
    /// CurrentVersion\Run key.
    /// </param>
    public RunKeyService(string valueName, string? subKeyPath = null)
    {
        _valueName = valueName;
        _subKeyPath = subKeyPath ?? DefaultSubKeyPath;
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath);
        return key?.GetValue(_valueName) is string;
    }

    public void Enable(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKeyPath, writable: true);
        key.SetValue(_valueName, $"\"{executablePath}\"");
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
