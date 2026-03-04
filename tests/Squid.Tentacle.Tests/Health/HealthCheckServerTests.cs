using System.Net;
using Squid.Tentacle.Health;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Collections;
using Squid.Tentacle.Tests.Support.Environment;

namespace Squid.Tentacle.Tests.Health;

[Trait("Category", TentacleTestCategories.Integration)]
[Collection(TentacleProcessIntegrationCollection.Name)]
public class HealthCheckServerTests : TimedTestBase
{
    [Fact]
    public async Task Liveness_Endpoints_Return_200()
    {
        var ready = true;
        await using var server = StartServer(out var port, () => ready);
        using var client = new HttpClient();

        var healthz = await GetAsync(client, port, "/healthz");
        var alias = await GetAsync(client, port, "/health/liveness");

        healthz.StatusCode.ShouldBe(HttpStatusCode.OK);
        healthz.Body.ShouldContain("alive");
        alias.StatusCode.ShouldBe(HttpStatusCode.OK);
        alias.Body.ShouldContain("alive");
    }

    [Fact]
    public async Task Readiness_Endpoints_Reflect_Callback_State()
    {
        var ready = false;
        await using var server = StartServer(out var port, () => ready);
        using var client = new HttpClient();

        var notReady = await GetAsync(client, port, "/readyz");
        notReady.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        notReady.Body.ShouldContain("not ready");

        ready = true;

        var readyResponse = await GetAsync(client, port, "/health/readiness");
        readyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        readyResponse.Body.ShouldContain("ready");
    }

    [Fact]
    public async Task Unknown_Path_Returns_404()
    {
        await using var server = StartServer(out var port, () => true);
        using var client = new HttpClient();

        var response = await GetAsync(client, port, "/nope");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Body.ShouldContain("not found");
    }

    private static HealthCheckServer StartServer(out int port, Func<bool> readiness)
    {
        port = TcpPortAllocator.GetEphemeralPort();
        var server = new HealthCheckServer(port, readiness);
        server.Start();
        return server;
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> GetAsync(HttpClient client, int port, string path)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        Exception lastException = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}{path}", TestCancellationToken)
                    .ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(TestCancellationToken).ConfigureAwait(false);
                return (response.StatusCode, body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
                await Task.Delay(50, TestCancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"Timed out calling health endpoint {path} on port {port}", lastException);
    }
}
