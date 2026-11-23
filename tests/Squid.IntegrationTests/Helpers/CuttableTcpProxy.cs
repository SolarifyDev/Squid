using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Squid.IntegrationTests.Helpers;

/// <summary>
/// A TCP forwarder that stands between a client and a server (here, Squid and Redis) so a test can cut the "network" and bring it back, and see every byte the
/// client sent. <see cref="Sever"/> stops listening and drops every live connection (clients then get connection-refused, like a host that is gone);
/// <see cref="Restore"/> listens again on the same port. What the client sent is kept (up to a cap) so a test can prove what did, and did not, cross the wire.
/// </summary>
public sealed class CuttableTcpProxy : IDisposable
{
    private const int MaxRecordedBytes = 64 * 1024 * 1024;

    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly object _gate = new();
    private readonly List<TcpClient> _live = new();
    private readonly StringBuilder _sent = new();
    private TcpListener _listener;
    private bool _disposed;

    public CuttableTcpProxy(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        Port = FreePort();

        Listen();
    }

    public int Port { get; }

    /// <summary>The connection string to hand to a client in place of the real target.</summary>
    public string Address => $"127.0.0.1:{Port}";

    public void Sever()
    {
        lock (_gate)
        {
            _listener?.Stop();
            _listener = null;

            foreach (var connection in _live) connection.Dispose();

            _live.Clear();
        }
    }

    public void Restore()
    {
        lock (_gate)
        {
            if (_listener != null) return;
        }

        Listen();
    }

    /// <summary>A position in what the client has sent so far; pass it to <see cref="SentSince"/>.</summary>
    public int Mark()
    {
        lock (_sent) return _sent.Length;
    }

    /// <summary>What the client sent after <paramref name="mark"/>, as Latin-1 text (every byte kept, so binary payloads can be searched too).</summary>
    public string SentSince(int mark)
    {
        lock (_sent) return _sent.ToString(mark, _sent.Length - mark);
    }

    public void Dispose()
    {
        _disposed = true;

        Sever();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop();

        return port;
    }

    private void Listen()
    {
        TcpListener listener;

        lock (_gate)
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            _listener = listener;
        }

        _ = Task.Run(() => AcceptLoopAsync(listener));
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_disposed)
        {
            TcpClient inbound;

            try
            {
                inbound = await listener.AcceptTcpClientAsync();
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => PipeAsync(inbound));
        }
    }

    private async Task PipeAsync(TcpClient inbound)
    {
        var outbound = new TcpClient();

        try
        {
            await outbound.ConnectAsync(_targetHost, _targetPort);

            lock (_gate)
            {
                _live.Add(inbound);
                _live.Add(outbound);
            }

            await Task.WhenAny(RecordAndForwardAsync(inbound.GetStream(), outbound.GetStream()), outbound.GetStream().CopyToAsync(inbound.GetStream()));
        }
        catch
        {
            // severed or refused: the point of this class
        }
        finally
        {
            inbound.Dispose();
            outbound.Dispose();
        }
    }

    private async Task RecordAndForwardAsync(Stream from, Stream to)
    {
        var buffer = new byte[16 * 1024];
        int read;

        while ((read = await from.ReadAsync(buffer)) > 0)
        {
            Record(buffer, read);

            await to.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    private void Record(byte[] buffer, int count)
    {
        lock (_sent)
        {
            if (_sent.Length < MaxRecordedBytes) _sent.Append(Encoding.Latin1.GetString(buffer, 0, count));
        }
    }
}
