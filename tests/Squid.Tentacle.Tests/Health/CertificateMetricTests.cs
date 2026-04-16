using Squid.Tentacle.Health;

namespace Squid.Tentacle.Tests.Health;

/// <summary>
/// Guards the cert-expiry gauge contract:
///  * Starts at -1 (sentinel — not yet set)
///  * Exporter omits the metric when sentinel
///  * Exporter emits the gauge once a real value is published
/// </summary>
[Collection(TentacleMetricsCollection.Name)]
public class CertificateMetricTests : IDisposable
{
    public CertificateMetricTests() => TentacleMetrics.Reset();

    public void Dispose() => TentacleMetrics.Reset();

    [Fact]
    public void CertificateExpiresInDays_DefaultsToSentinel_MinusOne()
    {
        TentacleMetrics.CertificateExpiresInDays.ShouldBe(-1);
    }

    [Fact]
    public void Exporter_OmitsCertMetric_WhenSentinel()
    {
        // Before the cert loader publishes, Prometheus should see NO
        // squid_tentacle_certificate_expires_in_days metric — otherwise the
        // -1 would alert on every deploy as "cert expired" during the
        // startup window.
        var output = MetricsExporter.ExportPrometheus();

        output.ShouldNotContain("squid_tentacle_certificate_expires_in_days");
    }

    [Fact]
    public void Exporter_EmitsCertMetric_OnceSet()
    {
        TentacleMetrics.SetCertificateExpiresInDays(36500);   // 100 years

        var output = MetricsExporter.ExportPrometheus();

        output.ShouldContain("# HELP squid_tentacle_certificate_expires_in_days");
        output.ShouldContain("# TYPE squid_tentacle_certificate_expires_in_days gauge");
        output.ShouldContain("squid_tentacle_certificate_expires_in_days 36500");
    }

    [Fact]
    public void SetCertificateExpiresInDays_IsInterlocked_AndLatestWins()
    {
        TentacleMetrics.SetCertificateExpiresInDays(100);
        TentacleMetrics.SetCertificateExpiresInDays(50);

        TentacleMetrics.CertificateExpiresInDays.ShouldBe(50);
    }

    [Fact]
    public void Reset_ReturnsToSentinel()
    {
        TentacleMetrics.SetCertificateExpiresInDays(365);
        TentacleMetrics.Reset();

        TentacleMetrics.CertificateExpiresInDays.ShouldBe(-1);
    }
}
