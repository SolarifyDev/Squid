using Shouldly;
using Squid.Core.Halibut.RedisQueue;
using Squid.Core.Halibut.Resilience;
using Xunit;
using static Squid.UnitTests.Halibut.RedisQueue.RedisQueueTestSupport;

namespace Squid.UnitTests.Halibut.RedisQueue;

public sealed class PollerPresenceTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    private readonly ManualTimeProvider _clock = new();

    [Fact]
    public void NeverSeen_IsNotLive()
    {
        new PollerPresence(_clock, Window).IsLive.ShouldBeFalse("a pod that has never seen the agent must not claim it");
    }

    [Fact]
    public void OutstandingDequeue_IsLive_HoweverLongItWaits()
    {
        var presence = new PollerPresence(_clock, Window);

        using var waiting = presence.DequeueStarted();

        _clock.Advance(TimeSpan.FromMinutes(10));

        presence.IsLive.ShouldBeTrue();
    }

    [Theory]
    [InlineData(14_999, true)]
    [InlineData(15_000, false)]
    [InlineData(15_001, false)]
    public void AfterTheLastDequeueEnds_IsLiveForExactlyTheWindow(int millisecondsLater, bool live)
    {
        var presence = new PollerPresence(_clock, Window);

        presence.DequeueStarted().Dispose();
        _clock.Advance(TimeSpan.FromMilliseconds(millisecondsLater));

        presence.IsLive.ShouldBe(live);
    }

    [Fact]
    public void RequestInFlight_IsLive_UntilItsResponseIsApplied_ThenForTheWindow()
    {
        var presence = new PollerPresence(_clock, Window);
        var id = Guid.NewGuid();

        presence.DequeueStarted().Dispose();
        presence.RequestHandedOut(id);
        _clock.Advance(TimeSpan.FromMinutes(30));

        presence.IsLive.ShouldBeTrue("the agent is busy on a long request, not gone");

        presence.ResponseApplied(id);
        _clock.Advance(TimeSpan.FromSeconds(14));
        presence.IsLive.ShouldBeTrue();

        _clock.Advance(TimeSpan.FromSeconds(2));
        presence.IsLive.ShouldBeFalse();
    }

    [Fact]
    public void ADequeueScopeDisposedTwice_EndsOnlyOnce()
    {
        var presence = new PollerPresence(_clock, Window);
        var first = presence.DequeueStarted();

        using var second = presence.DequeueStarted();

        first.Dispose();
        first.Dispose();
        _clock.Advance(TimeSpan.FromMinutes(1));

        presence.IsLive.ShouldBeTrue("the second dequeue is still outstanding");
    }

    [Fact]
    public void AnUnknownResponse_DoesNotMakeAnythingLive()
    {
        var presence = new PollerPresence(_clock, Window);

        presence.ResponseApplied(Guid.NewGuid());
        _clock.Advance(TimeSpan.FromMinutes(1));

        presence.IsLive.ShouldBeFalse();
    }
}

public sealed class RedisQueueHealthTests
{
    private readonly ManualTimeProvider _clock = new();

    private RedisQueueHealth Health() => new(Options(), _clock);

    [Fact]
    public void StartsClosedAndAvailable()
    {
        var health = Health();

        health.State.ShouldBe(CircuitBreakerState.Closed);
        health.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public void ConsecutiveFailuresOpenTheBreaker_AtTheThreshold()
    {
        var health = Health();

        health.RecordFailure("a");
        health.RecordFailure("b");
        health.IsAvailable.ShouldBeTrue("two failures are below the threshold of three");

        health.RecordFailure("c");

        health.State.ShouldBe(CircuitBreakerState.Open);
        health.IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public void ASuccessInBetween_ResetsTheCount()
    {
        var health = Health();

        health.RecordFailure("a");
        health.RecordFailure("b");
        health.RecordSuccess();
        health.RecordFailure("c");
        health.RecordFailure("d");

        health.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public void AfterTheOpenDuration_IsHalfOpen_ButStillNotAvailableToRequests()
    {
        var health = Health();

        Open(health);
        _clock.Advance(TimeSpan.FromMilliseconds(4_999));
        health.State.ShouldBe(CircuitBreakerState.Open);

        _clock.Advance(TimeSpan.FromMilliseconds(1));

        health.State.ShouldBe(CircuitBreakerState.HalfOpen);
        health.IsAvailable.ShouldBeFalse("only the ping may close it; a user request is never the probe");
    }

    [Fact]
    public void HalfOpen_SuccessCloses_FailureReopens()
    {
        var health = Health();

        Open(health);
        _clock.Advance(TimeSpan.FromSeconds(5));
        health.RecordFailure("ping");

        health.State.ShouldBe(CircuitBreakerState.Open, "a failed probe re-opens it for another full duration");

        _clock.Advance(TimeSpan.FromSeconds(5));
        health.RecordSuccess();

        health.State.ShouldBe(CircuitBreakerState.Closed);
        health.IsAvailable.ShouldBeTrue();
    }

    private static void Open(RedisQueueHealth health)
    {
        for (var i = 0; i < 3; i++) health.RecordFailure("down");
    }
}

public sealed class RedisHealthProbeTests
{
    private readonly ManualTimeProvider _clock = new();
    private readonly ScriptedPing _ping = new();

    private (RedisQueueHealth Health, RedisHealthProbe Probe) Build()
    {
        var health = new RedisQueueHealth(Options(), _clock);

        return (health, new RedisHealthProbe(health, _ping, Options()));
    }

    [Fact]
    public async Task FailingPings_OpenTheBreaker_AndASuccessfulOneAfterTheOpenDurationClosesIt()
    {
        var (health, probe) = Build();

        _ping.Fail = true;

        for (var i = 0; i < 3; i++) await probe.ProbeOnceAsync(CancellationToken.None);

        health.IsAvailable.ShouldBeFalse();

        var pingsWhileOpen = _ping.Calls;

        await probe.ProbeOnceAsync(CancellationToken.None);

        _ping.Calls.ShouldBe(pingsWhileOpen, "a Redis that is down is not pinged while the breaker rests");

        _clock.Advance(TimeSpan.FromSeconds(5));
        _ping.Fail = false;
        await probe.ProbeOnceAsync(CancellationToken.None);

        health.IsAvailable.ShouldBeTrue("recovery needs no restart and no request");
    }

    [Fact]
    public async Task APingStillFailingInHalfOpen_KeepsTheBreakerOpen()
    {
        var (health, probe) = Build();

        _ping.Fail = true;

        for (var i = 0; i < 3; i++) await probe.ProbeOnceAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await probe.ProbeOnceAsync(CancellationToken.None);

        health.State.ShouldBe(CircuitBreakerState.Open);
    }

    [Fact]
    public async Task AShutdownDuringAPing_IsNotAFailure()
    {
        var (health, probe) = Build();
        using var stop = new CancellationTokenSource();

        _ping.Block = true;

        var probing = probe.ProbeOnceAsync(stop.Token);

        await stop.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => probing);
        health.State.ShouldBe(CircuitBreakerState.Closed);
    }

    private sealed class ScriptedPing : IRedisPing
    {
        private int _calls;

        public bool Fail { get; set; }

        public bool Block { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public async Task PingAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);

            if (Block) await Task.Delay(Timeout.Infinite, ct);

            if (Fail) throw new TimeoutException("no answer");
        }
    }
}
