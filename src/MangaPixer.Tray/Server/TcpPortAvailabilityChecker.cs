using System.Net;
using System.Net.Sockets;

namespace com.lifepixer.mangapixer.Tray.Server;

/// <summary>
/// Probes a port by binding a loopback <see cref="TcpListener"/> and
/// immediately releasing it. Binding loopback (rather than the LAN-mode
/// 0.0.0.0) is enough to detect both failure modes Kestrel can hit —
/// "already in use" and a Windows-reserved excluded port range — since both
/// apply system-wide regardless of which address is ultimately bound.
/// </summary>
public sealed class TcpPortAvailabilityChecker : IPortAvailabilityChecker
{
    public bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
