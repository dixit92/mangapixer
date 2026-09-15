namespace com.lifepixer.mangaplex.Tray.Server;

/// <summary>
/// Abstracts a loopback TCP bind/release probe so <see cref="ServerPortResolver"/>
/// is unit-testable without touching real sockets.
/// </summary>
public interface IPortAvailabilityChecker
{
    bool IsPortAvailable(int port);
}
