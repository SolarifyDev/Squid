using System.Net;
using System.Text;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Registration;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Collections;
using Squid.Tentacle.Tests.Support.Environment;

namespace Squid.Tentacle.Tests.Registration;

[Trait("Category", TentacleTestCategories.Integration)]
[Collection(TentacleProcessIntegrationCollection.Name)]
public class TentacleRegistrationClientReliabilityTests : TimedTestBase
{
    [Fact]
    public async Task RegisterAsync_DoesNotRetry_On_ClientError()
    {
        await using var server = ScriptedRegistrationServer.Start();
        server.Enqueue(HttpStatusCode.Unauthorized, """{"error":"nope"}""");

        var client = CreateClient(server, CreateFastRetryOptions(maxRetries: 3));

        await Should.ThrowAsync<HttpRequestException>(() =>
            client.RegisterAsync("sub-1", "thumb-1", TestCancellationToken));

        server.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task RegisterAsync_Retries_On_ServerError_And_Then_Succeeds()
    {
        await using var server = ScriptedRegistrationServer.Start();
        server.Enqueue(HttpStatusCode.InternalServerError, """{"error":"temporary"}""");
        server.Enqueue(HttpStatusCode.OK, """
        {"data":{"machineId":7,"serverThumbprint":"SERVER","subscriptionUri":"poll://sub-2/"}}
        """);

        var client = CreateClient(server, CreateFastRetryOptions(maxRetries: 3));

        var result = await client.RegisterAsync("sub-2", "thumb-2", TestCancellationToken);

        result.MachineId.ShouldBe(7);
        result.ServerThumbprint.ShouldBe("SERVER");
        result.SubscriptionUri.ShouldBe("poll://sub-2/");
        server.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task RegisterAsync_Falls_Back_To_Default_SubscriptionUri_When_Response_Missing_Field()
    {
        await using var server = ScriptedRegistrationServer.Start();
        server.Enqueue(HttpStatusCode.OK, """
        {"data":{"machineId":9,"serverThumbprint":"SERVER2"}}
        """);

        var client = CreateClient(server, CreateFastRetryOptions(maxRetries: 2));

        var result = await client.RegisterAsync("sub-missing-uri", "thumb", TestCancellationToken);

        result.SubscriptionUri.ShouldBe("poll://sub-missing-uri/");
        server.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task RegisterAsync_Allows_Short_SubscriptionId_When_Defaulting_MachineName()
    {
        await using var server = ScriptedRegistrationServer.Start();
        server.Enqueue(HttpStatusCode.OK, """
        {"data":{"machineId":10,"serverThumbprint":"SERVER3","subscriptionUri":"poll://sub/"}}
        """);

        var client = CreateClient(server, CreateFastRetryOptions(maxRetries: 2));

        var result = await client.RegisterAsync("sub", "thumb", TestCancellationToken);

        result.MachineId.ShouldBe(10);
        server.LastRequestBody.ShouldContain("\"machineName\":\"k8s-tentacle-sub\"");
    }

    private static TentacleRegistrationClient CreateClient(
        ScriptedRegistrationServer server,
        TentacleRegistrationClientOptions options)
    {
        return new TentacleRegistrationClient(
            new TentacleSettings
            {
                ServerUrl = server.BaseAddress.ToString().TrimEnd('/'),
                BearerToken = "token"
            },
            new KubernetesSettings(),
            options);
    }

    private static TentacleRegistrationClientOptions CreateFastRetryOptions(int maxRetries)
    {
        return new TentacleRegistrationClientOptions
        {
            MaxRetries = maxRetries,
            InitialDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero,
            DelayAsync = static (_, _) => Task.CompletedTask
        };
    }

    private sealed class ScriptedRegistrationServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loopTask;
        private readonly Queue<(HttpStatusCode StatusCode, string Body)> _responses = new();
        private int _requestCount;
        private string _lastRequestBody = string.Empty;

        private ScriptedRegistrationServer(int port)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseAddress.ToString());
            _listener.Start();
            _loopTask = LoopAsync(_cts.Token);
        }

        public Uri BaseAddress { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public string LastRequestBody => Volatile.Read(ref _lastRequestBody);

        public static ScriptedRegistrationServer Start()
            => new(TcpPortAllocator.GetEphemeralPort());

        public void Enqueue(HttpStatusCode statusCode, string body)
            => _responses.Enqueue((statusCode, body));

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx = null;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref _requestCount);
                    using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                    {
                        Volatile.Write(ref _lastRequestBody, await reader.ReadToEndAsync(ct).ConfigureAwait(false));
                    }

                    if (!_responses.TryDequeue(out var response))
                    {
                        response = (HttpStatusCode.InternalServerError, """{"error":"no scripted response"}""");
                    }

                    var bytes = Encoding.UTF8.GetBytes(response.Body);
                    ctx.Response.StatusCode = (int)response.StatusCode;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                    ctx.Response.Close();
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    if (ctx != null)
                    {
                        try
                        {
                            ctx.Response.StatusCode = 500;
                            ctx.Response.Close();
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
            }
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
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            finally
            {
                _listener.Close();
                _cts.Dispose();
            }
        }
    }
}
