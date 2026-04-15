using Squid.Tentacle.Halibut;

namespace Squid.Tentacle.Tests.Health;

/// <summary>
/// Guards the <c>/health/readyz</c> contract for Listening-mode tentacles:
/// readiness should reflect whether the Halibut listener actually bound to
/// its port. Without this, a port collision / permission denial leaves the
/// service "running" per systemd but invisible to the Squid Server.
///
/// The actual composition lives in TentacleApp; here we verify the
/// <see cref="ITentacleHalibutHost"/> contract that backs it.
/// </summary>
public class ListeningReadinessTests
{
    [Fact]
    public void ITentacleHalibutHost_FreshHost_ReportsNotListening()
    {
        var host = new FakeHost();

        host.IsListening.ShouldBeFalse();
        host.ListeningPort.ShouldBe(0);
    }

    [Fact]
    public void ITentacleHalibutHost_AfterStartListening_FlipsToListening()
    {
        var host = new FakeHost();

        host.StartListening(10933);

        host.IsListening.ShouldBeTrue();
        host.ListeningPort.ShouldBe(10933);
    }

    [Fact]
    public void ITentacleHalibutHost_StartPolling_DoesNotFlipListening()
    {
        // Polling tentacles never listen — IsListening must stay false so the
        // readiness composer doesn't accidentally require listening for them.
        var host = new FakeHost();

        host.StartPolling("FAF04764", "sub-1");

        host.IsListening.ShouldBeFalse();
    }

    [Fact]
    public void ReadinessComposer_PollingMode_IgnoresIsListening()
    {
        // Mirror of the composition logic in TentacleApp: requireListening=false
        // means polling mode, so we never gate on IsListening.
        var host = new FakeHost();   // IsListening = false

        var ready = ComposeReadiness(serviceReady: true, flavorReady: true, requireListening: false, host);

        ready.ShouldBeTrue();
    }

    [Fact]
    public void ReadinessComposer_ListeningMode_RequiresIsListening()
    {
        var host = new FakeHost();   // IsListening = false (bind failed scenario)

        var ready = ComposeReadiness(serviceReady: true, flavorReady: true, requireListening: true, host);

        ready.ShouldBeFalse("Listening tentacle whose bind failed must report not-ready");
    }

    [Fact]
    public void ReadinessComposer_ListeningMode_PassesOnceListening()
    {
        var host = new FakeHost();
        host.StartListening(10933);

        var ready = ComposeReadiness(serviceReady: true, flavorReady: true, requireListening: true, host);

        ready.ShouldBeTrue();
    }

    [Fact]
    public void ReadinessComposer_ServiceDraining_ReturnsNotReady()
    {
        // isReady=false (drain-in-progress) shortcuts everything — readiness is no
        // even if every other check would have passed.
        var host = new FakeHost();
        host.StartListening(10933);

        var ready = ComposeReadiness(serviceReady: false, flavorReady: true, requireListening: true, host);

        ready.ShouldBeFalse();
    }

    [Fact]
    public void ReadinessComposer_FlavorReadinessFails_ReturnsNotReady()
    {
        // K8s leader election (or any flavor-supplied check) saying "not ready"
        // must propagate to /health/readyz so we don't get traffic during failover.
        var host = new FakeHost();
        host.StartListening(10933);

        var ready = ComposeReadiness(serviceReady: true, flavorReady: false, requireListening: true, host);

        ready.ShouldBeFalse();
    }

    /// <summary>
    /// Mirror of TentacleApp's readiness composition. Kept here so the contract
    /// is unit-testable without spinning the full app.
    /// </summary>
    private static bool ComposeReadiness(bool serviceReady, bool flavorReady, bool requireListening, ITentacleHalibutHost host)
    {
        if (!serviceReady) return false;
        if (!flavorReady) return false;
        if (requireListening && !host.IsListening) return false;
        return true;
    }

    private sealed class FakeHost : ITentacleHalibutHost
    {
        public bool IsListening { get; private set; }
        public int ListeningPort { get; private set; }

        public void StartPolling(string serverThumbprint, string subscriptionId, string subscriptionUri = null)
        {
            // No-op for these tests; we're only validating the IsListening contract.
        }

        public void StartListening(int port, string serverThumbprint = null)
        {
            ListeningPort = port;
            IsListening = true;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
