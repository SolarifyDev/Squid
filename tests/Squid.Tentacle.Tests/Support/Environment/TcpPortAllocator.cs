using System.Net;
using System.Net.Sockets;

namespace Squid.Tentacle.Tests.Support.Environment;

public static class TcpPortAllocator
{
    public static int GetEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
