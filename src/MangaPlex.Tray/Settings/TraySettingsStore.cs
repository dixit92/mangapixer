using System.Text.Json;

namespace com.lifepixer.mangaplex.Tray.Settings;

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
        "MangaPlex");

    /// <summary>
    /// Returns defaults when the file is missing or unreadable — a corrupt
    /// settings file must never keep the tray from starting.
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
        catch (JsonException)
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
