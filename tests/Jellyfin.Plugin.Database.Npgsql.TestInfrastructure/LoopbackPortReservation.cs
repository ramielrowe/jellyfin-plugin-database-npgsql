using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

public sealed class LoopbackPortReservation : IDisposable
{
    private readonly FileStream _lease;
    private TcpListener? _listener;

    private LoopbackPortReservation(int port, FileStream lease, TcpListener listener)
    {
        Port = port;
        _lease = lease;
        _listener = listener;
    }

    public int Port { get; }

    public static LoopbackPortReservation Acquire(string coordinationRoot)
    {
        var root = Path.GetFullPath(coordinationRoot);
        Directory.CreateDirectory(root);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var leasePath = Path.Combine(root, port.ToString(CultureInfo.InvariantCulture) + ".lock");
            try
            {
                var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new LoopbackPortReservation(port, lease, listener);
            }
            catch (IOException)
            {
                listener.Stop();
            }
        }

        throw new InvalidOperationException("Could not coordinate an unused loopback port after 100 attempts.");
    }

    public void ReleaseSocketForLaunch()
    {
        _listener?.Stop();
        _listener = null;
    }

    public void Dispose()
    {
        _listener?.Stop();
        _lease.Dispose();
    }
}
