namespace com.lifepixer.mangaplex.Tray.Server;

/// <summary>
/// Picks the port the tray actually launches the server on. Windows'
/// per-machine, per-reboot TCP port exclusion ranges (Hyper-V/WSL/Docker NAT
/// reservations) mean no single fixed default can be relied on across hosts,
/// so the preferred port is checked first and a small deterministic range
/// above it is scanned if it is unavailable.
/// </summary>
public sealed class ServerPortResolver
{
    private readonly IPortAvailabilityChecker _checker;

    public ServerPortResolver(IPortAvailabilityChecker? checker = null)
    {
        _checker = checker ?? new TcpPortAvailabilityChecker();
    }

    /// <summary>
    /// Returns <paramref name="preferredPort"/> if it is free, otherwise the
    /// first free port in the following <paramref name="maxCandidates"/> - 1
    /// ports, or null if none of them are free either.
    /// </summary>
    public int? ResolveAvailablePort(int preferredPort, int maxCandidates = 20)
    {
        for (var offset = 0; offset < maxCandidates; offset++)
        {
            var candidate = preferredPort + offset;
            if (_checker.IsPortAvailable(candidate))
                return candidate;
        }

        return null;
    }
}
