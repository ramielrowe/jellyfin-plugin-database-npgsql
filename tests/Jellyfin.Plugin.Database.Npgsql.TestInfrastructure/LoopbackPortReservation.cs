using System;
using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

public static class LoopbackPortReservation
{
    public static int GetAvailablePort()
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
