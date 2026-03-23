using Squid.Tentacle.Configuration;
using Squid.Tentacle.Health;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Health;

public class KubernetesApiHealthProbeTests
{
    private readonly Mock<IKubernetesPodOperations> _ops = new();
    private readonly KubernetesSettings _settings = new() { TentacleNamespace = "test-ns" };

    [Fact]
    public void Check_ApiReachable_ReturnsHealthy()
    {
        _ops.Setup(o => o.NamespaceExists("test-ns")).Returns(true);

        var probe = new KubernetesApiHealthProbe(_ops.Object, _settings);
        probe.Check();

        probe.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public void Check_ApiUnreachable_ThreeConsecutiveFailures_Unhealthy()
    {
        _ops.Setup(o => o.NamespaceExists(It.IsAny<string>()))
            .Throws(new Exception("connection refused"));

        var probe = new KubernetesApiHealthProbe(_ops.Object, _settings);

        probe.Check();
        probe.IsHealthy.ShouldBeTrue(); // 1 failure < threshold

        probe.Check();
        probe.IsHealthy.ShouldBeTrue(); // 2 failures < threshold

        probe.Check();
        probe.IsHealthy.ShouldBeFalse(); // 3 failures = threshold
    }

    [Fact]
    public void Check_SingleFailure_StillHealthy()
    {
        _ops.Setup(o => o.NamespaceExists(It.IsAny<string>()))
            .Throws(new Exception("transient"));

        var probe = new KubernetesApiHealthProbe(_ops.Object, _settings);
        probe.Check();

        probe.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public void Check_RecoveryAfterFailures_ResetsCounter()
    {
        var callCount = 0;
        _ops.Setup(o => o.NamespaceExists("test-ns"))
            .Returns(() =>
            {
                callCount++;
                if (callCount <= 3)
                    throw new Exception("transient");
                if (callCount == 5)
                    throw new Exception("transient again");
                return true;
            });

        var probe = new KubernetesApiHealthProbe(_ops.Object, _settings);

        // Fail 3x → unhealthy
        probe.Check(); // 1
        probe.Check(); // 2
        probe.Check(); // 3
        probe.IsHealthy.ShouldBeFalse();

        // Succeed once → healthy, counter resets
        probe.Check(); // 4 — success
        probe.IsHealthy.ShouldBeTrue();

        // Fail once more → still healthy (counter was reset)
        probe.Check(); // 5 — failure
        probe.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public void IsHealthy_DefaultsToTrue()
    {
        var probe = new KubernetesApiHealthProbe(_ops.Object, _settings);

        probe.IsHealthy.ShouldBeTrue();
    }

    // ========== Latency Measurement ==========

    [Fact]
    public void Check_Success_RecordsLatency()
    {
        _ops.Setup(o => o.NamespaceExists("test-ns")).Returns(true);

        var probe = new KubernetesApiHealthProbe(_ops.Object, _settings);
        probe.Check();

        probe.LastLatencyMs.ShouldBeGreaterThanOrEqualTo(0);
        probe.LastLatencyMs.ShouldBeLessThan(1000);
    }

    [Fact]
    public void Check_Failure_RecordsLatency()
    {
        _ops.Setup(o => o.NamespaceExists(It.IsAny<string>()))
            .Callback(() => Thread.Sleep(50))
            .Throws(new Exception("timeout"));

        var probe = new KubernetesApiHealthProbe(_ops.Object, _settings);
        probe.Check();

        probe.LastLatencyMs.ShouldBeGreaterThanOrEqualTo(40);
    }

    [Fact]
    public void Check_SlowButSuccessful_StillHealthy()
    {
        _ops.Setup(o => o.NamespaceExists("test-ns"))
            .Callback(() => Thread.Sleep(100))
            .Returns(true);

        var probe = new KubernetesApiHealthProbe(_ops.Object, _settings);
        probe.Check();

        probe.IsHealthy.ShouldBeTrue();
        probe.LastLatencyMs.ShouldBeGreaterThan(80);
    }
}
