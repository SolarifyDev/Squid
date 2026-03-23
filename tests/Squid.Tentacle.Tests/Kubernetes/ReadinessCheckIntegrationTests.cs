using System.Net;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Health;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Collections;
using Squid.Tentacle.Tests.Support.Environment;

namespace Squid.Tentacle.Tests.Kubernetes;

[Trait("Category", TentacleTestCategories.Integration)]
[Collection(TentacleProcessIntegrationCollection.Name)]
public class ReadinessCheckIntegrationTests : TimedTestBase, IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IKubernetesPodOperations> _podOps = new();
    private readonly KubernetesSettings _settings = new() { TentacleNamespace = "test-ns" };

    public ReadinessCheckIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"squid-readiness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task HealthServer_Readiness_ReflectsNfsWatchdogState()
    {
        var watchdog = new NfsWatchdog(_tempDir, _podOps.Object, _settings);
        Func<bool> readiness = () => watchdog.IsHealthy;

        var port = TcpPortAllocator.GetEphemeralPort();
        await using var server = new HealthCheckServer(port, readiness);
        server.Start();

        using var client = new HttpClient();

        var response = await RetryGetAsync(client, port, "/readyz");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CombinedReadiness_AllChecksHealthy_ReturnsReady()
    {
        var watchdog = new NfsWatchdog(_tempDir, _podOps.Object, _settings);
        var checks = new List<Func<bool>> { () => watchdog.IsHealthy, () => true };

        Func<bool> combined = () => checks.All(c => c());

        var port = TcpPortAllocator.GetEphemeralPort();
        await using var server = new HealthCheckServer(port, combined);
        server.Start();

        using var client = new HttpClient();

        var response = await RetryGetAsync(client, port, "/readyz");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CombinedReadiness_OneCheckUnhealthy_ReturnsNotReady()
    {
        var healthy = true;
        var checks = new List<Func<bool>> { () => healthy, () => true };

        Func<bool> combined = () => checks.All(c => c());

        var port = TcpPortAllocator.GetEphemeralPort();
        await using var server = new HealthCheckServer(port, combined);
        server.Start();

        using var client = new HttpClient();

        var readyResponse = await RetryGetAsync(client, port, "/readyz");
        readyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        healthy = false;

        var notReadyResponse = await RetryGetAsync(client, port, "/readyz");
        notReadyResponse.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> RetryGetAsync(HttpClient client, int port, string path)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        Exception lastException = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}{path}", TestCancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(TestCancellationToken).ConfigureAwait(false);
                return (response.StatusCode, body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
                await Task.Delay(50, TestCancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"Timed out calling {path} on port {port}", lastException);
    }

    public override void Dispose()
    {
        base.Dispose();

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // cleanup best-effort
        }
    }
}
