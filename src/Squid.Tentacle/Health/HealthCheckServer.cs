using System.Net;
using Serilog;

namespace Squid.Tentacle.Health;

public class HealthCheckServer : IHealthCheckServer
{
    private readonly HttpListener _listener;
    private readonly Func<bool> _readinessCheck;
    private CancellationTokenSource _cts;
    private Task _listenTask;

    public HealthCheckServer(int port, Func<bool> readinessCheck)
    {
        _readinessCheck = readinessCheck;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _listenTask = ListenAsync(_cts.Token);

        Log.Information("Health check server started on port {Port}", GetPort());
    }

    private int GetPort()
    {
        var prefix = _listener.Prefixes.FirstOrDefault() ?? "";
        var uri = new Uri(prefix.Replace("+", "localhost"));
        return uri.Port;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                HandleRequest(context);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Health check request failed");
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";
        int statusCode;
        string body;

        switch (path)
        {
            case "/healthz":
            case "/health/liveness":
                statusCode = 200;
                body = "{\"status\":\"alive\"}";
                break;

            case "/readyz":
            case "/health/readiness":
                var isReady = _readinessCheck();
                statusCode = isReady ? 200 : 503;
                body = isReady
                    ? "{\"status\":\"ready\"}"
                    : "{\"status\":\"not ready\"}";
                break;

            case "/metrics":
                statusCode = 200;
                body = MetricsExporter.ExportPrometheus();
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
                var metricsBuffer = System.Text.Encoding.UTF8.GetBytes(body);
                context.Response.ContentLength64 = metricsBuffer.Length;
                context.Response.OutputStream.Write(metricsBuffer, 0, metricsBuffer.Length);
                context.Response.Close();
                return;

            default:
                statusCode = 404;
                body = "{\"error\":\"not found\"}";
                break;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var buffer = System.Text.Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener.Stop();

        if (_listenTask != null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        _cts?.Dispose();
    }
}
