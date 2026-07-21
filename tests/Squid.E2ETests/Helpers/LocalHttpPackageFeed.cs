using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Squid.E2ETests.Helpers;

/// <summary>
/// Minimal in-process HTTP feed used by Deploy a Package e2e.
/// Serves a single package archive at <c>{base}/api/v2/package/{id}/{version}</c>
/// so <see cref="Squid.Core.Services.DeploymentExecution.Packages.HttpPackageContentFetcher"/>
/// can download it without nuget.org.
/// </summary>
public sealed class LocalHttpPackageFeed : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly byte[] _packageBytes;
    private readonly string _packageId;
    private readonly string _version;

    public Uri BaseUri { get; }
    public int Port { get; }

    private LocalHttpPackageFeed(HttpListener listener, int port, string packageId, string version, byte[] packageBytes)
    {
        _listener = listener;
        Port = port;
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _packageId = packageId;
        _version = version;
        _packageBytes = packageBytes;
        _loop = Task.Run(ListenLoopAsync);
    }

    public static LocalHttpPackageFeed Start(string packageId, string version, byte[] packageBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(packageBytes);

        var port = GetAvailablePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return new LocalHttpPackageFeed(listener, port, packageId, version, packageBytes);
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleRequest(ctx));
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath?.Trim('/') ?? string.Empty;
            var expected = $"api/v2/package/{_packageId}/{_version}";

            if (!string.Equals(path, expected, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 404;
                var msg = Encoding.UTF8.GetBytes($"Not found: /{path}");
                ctx.Response.OutputStream.Write(msg);
                ctx.Response.Close();
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength64 = _packageBytes.Length;
            ctx.Response.OutputStream.Write(_packageBytes, 0, _packageBytes.Length);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
