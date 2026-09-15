namespace com.lifepixer.mangaplex.Tests.Tray.Settings;

using com.lifepixer.mangaplex.Tray.Settings;
using Xunit;

public sealed class TraySettingsStoreTests : IDisposable
{
    private readonly string _directory;

    public TraySettingsStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "MangaPlexTrayTests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaults()
    {
        var store = new TraySettingsStore(_directory);

        var settings = store.Load();

        Assert.Equal(1, settings.SchemaVersion);
        Assert.False(settings.AllowLanAccess);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsValues()
    {
        var store = new TraySettingsStore(_directory);
        var saved = new TraySettings { AllowLanAccess = true };

        store.Save(saved);
        var loaded = store.Load();

        Assert.True(loaded.AllowLanAccess);
    }

    [Fact]
    public void Load_WhenFileIsCorrupt_ReturnsDefaultsInsteadOfThrowing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "tray-settings.json"), "{ not valid json");
        var store = new TraySettingsStore(_directory);

        var settings = store.Load();

        Assert.False(settings.AllowLanAccess);
    }

    [Fact]
    public void Load_WhenFileIsLockedByAnotherHandle_ReturnsDefaultsInsteadOfThrowing()
    {
        Directory.CreateDirectory(_directory);
        var filePath = Path.Combine(_directory, "tray-settings.json");
        File.WriteAllText(filePath, "{}");
        var store = new TraySettingsStore(_directory);

        // Simulates OneDrive/antivirus holding an exclusive lock at the
        // moment the tray tries to read its own settings file.
        using var exclusiveLock = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var settings = store.Load();

        Assert.False(settings.AllowLanAccess);
    }

    [Fact]
    public void Constructor_CreatesDirectoryWhenMissing()
    {
        Assert.False(Directory.Exists(_directory));

        _ = new TraySettingsStore(_directory);

        Assert.True(Directory.Exists(_directory));
    }
}
