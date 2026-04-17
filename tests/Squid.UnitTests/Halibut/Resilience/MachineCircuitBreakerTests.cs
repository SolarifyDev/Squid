using Shouldly;
using Squid.Core.Halibut.Resilience;

namespace Squid.UnitTests.Halibut.Resilience;

public sealed class MachineCircuitBreakerTests
{
    [Fact]
    public void NewBreaker_Closed_AllowsCalls()
    {
        var breaker = new MachineCircuitBreaker(machineId: 1, failureThreshold: 3, openDuration: TimeSpan.FromSeconds(60));

        breaker.State.ShouldBe(CircuitBreakerState.Closed);
        Should.NotThrow(() => breaker.ThrowIfOpen());
    }

    [Fact]
    public void FailuresBelowThreshold_StaysClosed()
    {
        var breaker = new MachineCircuitBreaker(1, 3, TimeSpan.FromSeconds(60));

        breaker.RecordFailure();
        breaker.RecordFailure();

        breaker.State.ShouldBe(CircuitBreakerState.Closed);
        Should.NotThrow(() => breaker.ThrowIfOpen());
    }

    [Fact]
    public void ConsecutiveFailuresAtThreshold_OpensBreaker()
    {
        var breaker = new MachineCircuitBreaker(1, 3, TimeSpan.FromSeconds(60));

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();

        breaker.State.ShouldBe(CircuitBreakerState.Open);
        Should.Throw<CircuitOpenException>(() => breaker.ThrowIfOpen());
    }

    [Fact]
    public void SuccessResetsFailureCounter()
    {
        var breaker = new MachineCircuitBreaker(1, 3, TimeSpan.FromSeconds(60));

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordSuccess();
        breaker.RecordFailure();
        breaker.RecordFailure();

        breaker.State.ShouldBe(CircuitBreakerState.Closed);
    }

    [Fact]
    public void AfterOpenDurationElapses_NextCall_TransitionsToHalfOpen()
    {
        var now = DateTimeOffset.Parse("2026-04-17T12:00:00Z");
        var breaker = new MachineCircuitBreaker(1, 2, TimeSpan.FromSeconds(30), () => now);

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.State.ShouldBe(CircuitBreakerState.Open);

        now = now.AddSeconds(31);

        breaker.State.ShouldBe(CircuitBreakerState.HalfOpen);
    }

    [Fact]
    public void HalfOpen_SuccessClosesBreaker()
    {
        var now = DateTimeOffset.Parse("2026-04-17T12:00:00Z");
        var breaker = new MachineCircuitBreaker(1, 2, TimeSpan.FromSeconds(30), () => now);

        breaker.RecordFailure();
        breaker.RecordFailure();
        now = now.AddSeconds(31);
        _ = breaker.State;   // trigger HalfOpen transition

        breaker.RecordSuccess();

        breaker.State.ShouldBe(CircuitBreakerState.Closed);
        breaker.ConsecutiveFailures.ShouldBe(0);
    }

    [Fact]
    public void HalfOpen_FailureReopensBreaker_AndResetsTimer()
    {
        var now = DateTimeOffset.Parse("2026-04-17T12:00:00Z");
        var breaker = new MachineCircuitBreaker(1, 2, TimeSpan.FromSeconds(30), () => now);

        breaker.RecordFailure();
        breaker.RecordFailure();
        now = now.AddSeconds(31);
        _ = breaker.State;   // trigger HalfOpen

        breaker.RecordFailure();

        breaker.State.ShouldBe(CircuitBreakerState.Open);
        now = now.AddSeconds(25);
        breaker.State.ShouldBe(CircuitBreakerState.Open, "open duration must restart after a failed half-open probe");
    }

    [Fact]
    public async Task ExecuteAsync_Closed_Success_ReturnsResultAndClosed()
    {
        var breaker = new MachineCircuitBreaker(1, 3, TimeSpan.FromSeconds(60));

        var result = await breaker.ExecuteAsync(() => Task.FromResult(42));

        result.ShouldBe(42);
        breaker.State.ShouldBe(CircuitBreakerState.Closed);
    }

    [Fact]
    public async Task ExecuteAsync_Closed_Failure_RecordsAndRethrows()
    {
        var breaker = new MachineCircuitBreaker(1, 2, TimeSpan.FromSeconds(60));

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await breaker.ExecuteAsync<int>(() => throw new InvalidOperationException("boom")));

        breaker.ConsecutiveFailures.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_OpenBreaker_FailsFast_WithoutCallingAction()
    {
        var breaker = new MachineCircuitBreaker(1, 1, TimeSpan.FromSeconds(60));
        breaker.RecordFailure();   // immediately open

        var called = false;
        await Should.ThrowAsync<CircuitOpenException>(async () =>
            await breaker.ExecuteAsync(() =>
            {
                called = true;
                return Task.FromResult(0);
            }));

        called.ShouldBeFalse("open breaker must fail-fast without invoking the wrapped action");
    }
}
