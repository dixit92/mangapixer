namespace com.lifepixer.mangaplex.Tests.Tray.Startup;

using com.lifepixer.mangaplex.Tray.Startup;
using Microsoft.Win32;
using Xunit;

/// <summary>
/// Exercises RunKeyService against a throwaway HKCU subkey (never the real
/// CurrentVersion\Run key) so tests never leave a real startup entry behind.
/// </summary>
public sealed class RunKeyServiceTests : IDisposable
{
    private readonly string _subKeyPath = $@"Software\MangaPlexTrayTests\{Guid.NewGuid():N}";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_subKeyPath, throwOnMissingSubKey: false);

    [Fact]
    public void IsEnabled_WhenKeyNeverCreated_ReturnsFalse()
    {
        var service = new RunKeyService("MangaPlex", _subKeyPath);

        Assert.False(service.IsEnabled());
    }

    [Fact]
    public void Enable_ThenIsEnabled_ReturnsTrue_AndStoresQuotedPath()
    {
        var service = new RunKeyService("MangaPlex", _subKeyPath);

        service.Enable(@"C:\Program Files\MangaPlex\MangaPixer.Tray.exe");

        Assert.True(service.IsEnabled());
        using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath);
        Assert.Equal("\"C:\\Program Files\\MangaPlex\\MangaPixer.Tray.exe\"", key!.GetValue("MangaPlex"));
    }

    [Fact]
    public void Disable_AfterEnable_RemovesValue()
    {
        var service = new RunKeyService("MangaPlex", _subKeyPath);
        service.Enable(@"C:\MangaPlex\MangaPixer.Tray.exe");

        service.Disable();

        Assert.False(service.IsEnabled());
    }

    [Fact]
    public void Disable_WhenNeverEnabled_DoesNotThrow()
    {
        var service = new RunKeyService("MangaPlex", _subKeyPath);

        service.Disable();
    }
}
