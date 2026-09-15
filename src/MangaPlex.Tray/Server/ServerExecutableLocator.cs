namespace com.lifepixer.mangaplex.Tray.Server;

/// <summary>
/// Locates <c>MangaPlex.Server.exe</c> relative to the tray executable's
/// directory. The distribution layout (the <c>Publish-Windows.ps1</c> staging
/// folder, and the MSI payload harvested from it) places the tray at the
/// root with the server under <c>server\</c>; the same-directory probe
/// covers ad-hoc/dev layouts where both exes sit side by side.
/// </summary>
public static class ServerExecutableLocator
{
    public const string ServerExecutableName = "MangaPlex.Server.exe";

    /// <summary>
    /// Returns the first existing candidate (<c>server\</c> first, then the
    /// tray's own directory), or the <c>server\</c> candidate when neither
    /// exists yet — so the caller's <see cref="FileNotFoundException"/>
    /// names the expected distribution-layout path.
    /// </summary>
    public static string Resolve(string trayDirectory)
    {
        var distributionLayout = Path.Combine(trayDirectory, "server", ServerExecutableName);
        var sameDirectory = Path.Combine(trayDirectory, ServerExecutableName);

        if (File.Exists(distributionLayout))
            return distributionLayout;
        if (File.Exists(sameDirectory))
            return sameDirectory;
        return distributionLayout;
    }
}
