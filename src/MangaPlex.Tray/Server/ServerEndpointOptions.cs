namespace com.lifepixer.mangaplex.Tray.Server;

/// <summary>
/// The bind address/port the tray launches <c>MangaPlex.Server.exe</c> with.
/// </summary>
public sealed class ServerEndpointOptions
{
    // Not a fixed contract — Windows' TCP port exclusion ranges
    // (Hyper-V/WSL/Docker NAT reservations) vary per machine and per reboot,
    // so this is only the first candidate ServerPortResolver tries; the
    // resolved port is what actually gets persisted and used.
    public const int DefaultPort = 27272;

    public int Port { get; init; } = DefaultPort;

    public bool AllowLanAccess { get; init; }

    /// <summary>
    /// Value for the child process's <c>ASPNETCORE_URLS</c> environment
    /// variable: loopback-only by default, <c>0.0.0.0</c> when the LAN
    /// toggle is on.
    /// </summary>
    public string BuildAspNetCoreUrls() => $"http://{(AllowLanAccess ? "0.0.0.0" : "127.0.0.1")}:{Port}";

    /// <summary>
    /// Always loopback, even when <see cref="AllowLanAccess"/> is on — a
    /// server bound to <c>0.0.0.0</c> still accepts connections on
    /// 127.0.0.1, and the tray's own health poll / "Open MangaPlex" always
    /// run on the same machine.
    /// </summary>
    public Uri BuildLocalBaseUri() => new($"http://127.0.0.1:{Port}");
}
