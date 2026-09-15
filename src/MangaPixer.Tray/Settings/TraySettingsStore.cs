using System.Text.Json;

namespace com.lifepixer.mangapixer.Tray.Settings;

/// <summary>
/// Loads/saves <see cref="TraySettings"/> as JSON under a directory the
/// caller supplies (production uses <see cref="DefaultDirectory"/>; tests
/// point at a temp directory so nothing touches the real profile).
/// </summary>
public sealed class TraySettingsStore
{
    private readonly string _filePath;

    public TraySettingsStore(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        _filePath = Path.Combine(directoryPath, "tray-settings.json");
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MangaPixer");

    /// <summary>
    /// Returns defaults when the file is missing, unparsable, or unreadable
    /// (e.g. locked by OneDrive/antivirus scanning) — a bad settings file
    /// must never keep the tray from starting.
    /// </summary>
    public TraySettings Load()
    {
        if (!File.Exists(_filePath))
            return new TraySettings();

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<TraySettings>(json) ?? new TraySettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new TraySettings();
        }
    }

    public void Save(TraySettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}
