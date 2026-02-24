using System.Net;
using System.Text;
using System.Text.Json;
using Squid.Tentacle.Tests.Support.Environment;

namespace Squid.Tentacle.Tests.Support.Fakes;

public sealed class FakeAgentRegistrationServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenTask;
    private readonly TaskCompletionSource<string> _firstRequestBodyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requestCount;
    private string _lastRequestBody = string.Empty;
    private string _lastAuthorizationHeader = string.Empty;

    private FakeAgentRegistrationServer(int port)
    {
        Port = port;
        BaseAddress = new Uri($"http://127.0.0.1:{port}/");

        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseAddress.ToString());
        _listener.Start();
        _listenTask = ListenAsync(_cts.Token);
    }

    public int Port { get; }
    public Uri BaseAddress { get; }
    public int RequestCount => Volatile.Read(ref _requestCount);
    public string LastRequestBody => Volatile.Read(ref _lastRequestBody);
    public string LastAuthorizationHeader => Volatile.Read(ref _lastAuthorizationHeader);

    public static FakeAgentRegistrationServer Start(int? port = null)
        => new(port ?? TcpPortAllocator.GetEphemeralPort());

    public async Task<string> WaitForFirstRegistrationAsync(CancellationToken ct)
    {
        using var registration = ct.Register(() => _firstRequestBodyTcs.TrySetCanceled(ct));
        return await _firstRequestBodyTcs.Task.ConfigureAwait(false);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context = null;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
                await HandleAsync(context, ct).ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                if (context != null)
                {
                    try
                    {
                        context.Response.StatusCode = 500;
                        context.Response.Close();
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var path = context.Request.Url?.AbsolutePath ?? string.Empty;

        if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && path.Equals("/api/agents/register", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

            Interlocked.Increment(ref _requestCount);
            Volatile.Write(ref _lastRequestBody, body);
            Volatile.Write(ref _lastAuthorizationHeader, context.Request.Headers["Authorization"] ?? string.Empty);
            _firstRequestBodyTcs.TrySetResult(body);

            var subscriptionUri = TryExtractSubscriptionUri(body);
            var responseJson = JsonSerializer.Serialize(new
            {
                data = new
                {
                    machineId = 1234,
                    serverThumbprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                    subscriptionUri
                }
            });

            var bytes = Encoding.UTF8.GetBytes(responseJson);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            context.Response.Close();
            return;
        }

        context.Response.StatusCode = 404;
        context.Response.Close();
    }

    private static string TryExtractSubscriptionUri(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("subscriptionId", out var subscriptionId)
                && subscriptionId.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(subscriptionId.GetString()))
            {
                return $"poll://{subscriptionId.GetString()}/";
            }
        }
        catch
        {
            // Use fallback below.
        }

        return "poll://fake-subscription/";
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        try
        {
            _listener.Stop();
        }
        catch
        {
            // ignore
        }

        try
        {
            await _listenTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            _listener.Close();
            _cts.Dispose();
        }
    }
}
