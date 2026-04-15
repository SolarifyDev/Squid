using System.Net;
using Squid.Tentacle.Health;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Collections;
using Squid.Tentacle.Tests.Support.Environment;

namespace Squid.Tentacle.Tests.Health;

[Trait("Category", TentacleTestCategories.Integration)]
[Collection(TentacleProcessIntegrationCollection.Name)]
public class MetricsEndpointIntegrationTests : TimedTestBase
{
    [Fact]
    public async Task Metrics_Endpoint_Returns_Prometheus_Format()
    {
        TentacleMetrics.Reset();
        TentacleMetrics.ScriptStarted();

        var port = TcpPortAllocator.GetEphemeralPort();
        await using var server = new HealthCheckServer(port, () => true);
        server.Start();

        using var client = new HttpClient();
        var (statusCode, body, contentType) = await GetAsync(client, port, "/metrics");

        statusCode.ShouldBe(HttpStatusCode.OK);
        contentType.ShouldStartWith("text/plain");
        body.ShouldContain("# HELP squid_tentacle_active_scripts");
        body.ShouldContain("# TYPE squid_tentacle_active_scripts gauge");
        body.ShouldContain("squid_tentacle_active_scripts 1");
        body.ShouldContain("squid_tentacle_scripts_started_total 1");

        // Cert metric must NOT appear before SetCertificateExpiresInDays is called —
        // protects Prometheus from alerting on the -1 sentinel during the brief
        // window between service start and TentacleApp loading the cert.
        body.ShouldNotContain("squid_tentacle_certificate_expires_in_days");

        TentacleMetrics.Reset();
    }

    [Fact]
    public async Task Metrics_Endpoint_ExposesCertificateExpiry_AfterPublication()
    {
        // Regression: end-to-end check that the cert-expiry gauge actually flows
        // from TentacleMetrics → MetricsExporter → /metrics HTTP response.
        // Catches wiring breakages between the publishing site (TentacleApp) and
        // the exposure site (HealthCheckServer) that unit tests on each side
        // individually wouldn't notice.
        TentacleMetrics.Reset();
        TentacleMetrics.SetCertificateExpiresInDays(36500);   // 100 years

        var port = TcpPortAllocator.GetEphemeralPort();
        await using var server = new HealthCheckServer(port, () => true);
        server.Start();

        using var client = new HttpClient();
        var (statusCode, body, _) = await GetAsync(client, port, "/metrics");

        statusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("# HELP squid_tentacle_certificate_expires_in_days");
        body.ShouldContain("# TYPE squid_tentacle_certificate_expires_in_days gauge");
        body.ShouldContain("squid_tentacle_certificate_expires_in_days 36500");

        TentacleMetrics.Reset();
    }

    private async Task<(HttpStatusCode StatusCode, string Body, string ContentType)> GetAsync(HttpClient client, int port, string path)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        Exception lastException = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}{path}", TestCancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(TestCancellationToken).ConfigureAwait(false);
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
                return (response.StatusCode, body, contentType);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
                await Task.Delay(50, TestCancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"Timed out calling endpoint {path} on port {port}", lastException);
    }
}
