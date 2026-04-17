using Shouldly;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Settings.Halibut;

namespace Squid.UnitTests.Halibut.Resilience;

public sealed class MachineCircuitBreakerRegistryTests
{
    [Fact]
    public void GetOrCreate_SameId_ReturnsSameBreaker()
    {
        var registry = new MachineCircuitBreakerRegistry(BuildSettings());

        var a = registry.GetOrCreate(42);
        var b = registry.GetOrCreate(42);

        a.ShouldBeSameAs(b);
    }

    [Fact]
    public void GetOrCreate_DifferentIds_IndependentBreakers()
    {
        var registry = new MachineCircuitBreakerRegistry(BuildSettings(failureThreshold: 2));

        var a = registry.GetOrCreate(1);
        var b = registry.GetOrCreate(2);

        a.RecordFailure();
        a.RecordFailure();

        a.State.ShouldBe(CircuitBreakerState.Open);
        b.State.ShouldBe(CircuitBreakerState.Closed);
    }

    [Fact]
    public void GetOrCreate_UsesConfiguredThresholds()
    {
        var registry = new MachineCircuitBreakerRegistry(BuildSettings(failureThreshold: 5, openDurationSeconds: 10));

        var breaker = registry.GetOrCreate(1);

        for (var i = 0; i < 4; i++) breaker.RecordFailure();
        breaker.State.ShouldBe(CircuitBreakerState.Closed);

        breaker.RecordFailure();
        breaker.State.ShouldBe(CircuitBreakerState.Open);
    }

    private static HalibutSetting BuildSettings(int failureThreshold = 3, int openDurationSeconds = 60)
        => new()
        {
            CircuitBreaker = new CircuitBreakerSettings
            {
                FailureThreshold = failureThreshold,
                OpenDurationSeconds = openDurationSeconds
            }
        };
}
