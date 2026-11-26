using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Halibut.Exceptions;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Machines;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// The V2 to V1 cancel fall-back shares ONE deadline (cancel budget + grace) with the V2 attempt and the capabilities refresh. A rolled-back agent answers V2 with
/// transport errors the retry loop keeps retrying for the whole budget, so the V2 attempt must be capped to leave the refresh and the V1 call their reserve.
/// Every test runs on a virtual clock the test advances by hand (the deadline, the retry back-off and the refresh delay all read it): no real sleeps decide an outcome,
/// the only real waits are bounds that make a regression fail instead of hang. Fidelity: HIGH for <see cref="ProtocolDowngrade"/> and the canceller seam
/// (real selector, dispatchers, sessions, retry handler); only the agents and the refresher are faked.
/// </summary>
public class CancelFallbackReserveTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan CancelBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(10);

    // 25s total - (10s refresh + 5s V1 reserve) = 10s for the V2 attempt.
    private static readonly TimeSpan DefaultV2Share = TimeSpan.FromSeconds(10);

    private readonly VirtualClock _clock = new();
    private readonly VirtualRefresher _refresher;
    private readonly Mock<IScriptAgentSession> _v2 = new();
    private readonly Mock<IScriptAgentSession> _v1 = new();
    private readonly Mock<IScriptDispatcherSelector> _selector = new();
    private readonly TaskCompletionSource<AgentScriptStatus> _v2Hangs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _v1Cancels;

    public CancelFallbackReserveTests()
    {
        _refresher = new VirtualRefresher(_clock) { RefreshTimeout = RefreshTimeout };

        _v2.SetupGet(s => s.Version).Returns(ScriptProtocolVersion.V2);
        _v2.Setup(s => s.CancelAsync(It.IsAny<ScriptTicket>())).Returns(_v2Hangs.Task);

        _v1.SetupGet(s => s.Version).Returns(ScriptProtocolVersion.V1);
        _v1.Setup(s => s.CancelAsync(It.IsAny<ScriptTicket>())).Returns(() =>
        {
            Interlocked.Increment(ref _v1Cancels);

            return Task.FromResult(Status());
        });

        var dispatcher = new Mock<IScriptDispatcher>();
        dispatcher.Setup(d => d.Open(It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptSessionContext>())).Returns(_v1.Object);
        _selector.Setup(s => s.ForVersion(ScriptProtocolVersion.V1)).Returns(dispatcher.Object);
    }

    // ── The share the V2 attempt may spend ───────────────────────────────────

    [Fact]
    public async Task AHungV2Attempt_IsCutAtItsShare_NotOneTickBefore_AndThenTheRefreshStarts()
    {
        _refresher.AgentAnswer = V1Only();

        var cancel = StartCancel(withFallback: true);

        _clock.Advance(DefaultV2Share - Ms);
        _refresher.Calls.ShouldBe(0, "the V2 attempt still has a tick of its share left");

        _clock.Advance(Ms);
        await _refresher.Started.WaitAsync(Bound);

        _clock.Advance(RefreshTimeout);
        (await cancel.WaitAsync(Bound)).Version.ShouldBe(ScriptProtocolVersion.V1);
        _v1Cancels.ShouldBe(1);
    }

    // The reserve, exactly: after the V2 attempt is cut, the one deadline still has the refresh timeout plus the V1 reserve left.
    [Fact]
    public async Task AfterTheV2Cut_TheOneDeadlineKeepsExactlyTheRefreshTimeoutPlusTheV1Reserve()
    {
        _refresher.AgentAnswer = V1Only();
        _refresher.HangUntilCancelled = true;

        var cancel = StartCancel(withFallback: true);

        _clock.Advance(DefaultV2Share);
        await _refresher.Started.WaitAsync(Bound);

        var reserve = RefreshTimeout + ProtocolDowngrade.V1CancelReserve;

        _clock.Advance(reserve - Ms);
        _refresher.TokenCancelled.ShouldBeFalse("the overall deadline must not fire before the reserve is spent");

        _clock.Advance(Ms);
        _refresher.TokenCancelled.ShouldBeTrue("the overall deadline is unchanged: cancel budget + grace");
        await Should.ThrowAsync<OperationCanceledException>(() => cancel).WaitAsync(Bound);
    }

    [Fact]
    public async Task AConfiguredRefreshTimeout_IsReservedToo()
    {
        _refresher.RefreshTimeout = TimeSpan.FromSeconds(12);
        _refresher.AgentAnswer = V1Only();

        var cancel = StartCancel(withFallback: true);

        // 25 - (12 + 5) = 8s for the V2 attempt.
        _clock.Advance(TimeSpan.FromSeconds(8) - Ms);
        _refresher.Calls.ShouldBe(0);

        _clock.Advance(Ms);
        await _refresher.Started.WaitAsync(Bound);

        _clock.Advance(TimeSpan.FromSeconds(1));
        (await cancel.WaitAsync(Bound)).Version.ShouldBe(ScriptProtocolVersion.V1);
    }

    [Fact]
    public async Task ASmallBudget_StillGivesV2AQuarterOfTheDeadline_NeverZero()
    {
        _refresher.AgentAnswer = V1Only();

        var cancel = StartCancel(withFallback: true, budget: TimeSpan.FromSeconds(3), grace: TimeSpan.FromSeconds(1));

        // total 4s is smaller than the reserve: the floor is a quarter, 1s.
        _clock.Advance(TimeSpan.FromSeconds(1) - Ms);
        _refresher.Calls.ShouldBe(0);

        _clock.Advance(Ms);
        await _refresher.Started.WaitAsync(Bound);

        _clock.Advance(TimeSpan.FromSeconds(1));
        (await cancel.WaitAsync(Bound)).Version.ShouldBe(ScriptProtocolVersion.V1);
    }

    // ── What is NOT capped ───────────────────────────────────────────────────

    [Fact]
    public async Task WithoutARefresher_NoFallbackCanFollow_SoTheV2AttemptKeepsTheWholeDeadline()
    {
        var cancel = StartCancel(withFallback: false);

        _clock.Advance(DefaultV2Share + TimeSpan.FromSeconds(5));
        cancel.IsCompleted.ShouldBeFalse("with nothing to fall back to, cutting V2 early only loses retries");

        _clock.Advance(CancelBudget + Grace);
        await Should.ThrowAsync<OperationCanceledException>(() => cancel).WaitAsync(Bound);
        _refresher.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AV1Cancel_KeepsTheWholeDeadline_NeverRefreshes()
    {
        _v1.Setup(s => s.CancelAsync(It.IsAny<ScriptTicket>())).Returns(new TaskCompletionSource<AgentScriptStatus>().Task);

        var cancel = ProtocolDowngrade.CancelAsync(_v1.Object, Context(withFallback: true), Target(), CancellationToken.None);

        _clock.Advance(DefaultV2Share + TimeSpan.FromSeconds(5));
        cancel.IsCompleted.ShouldBeFalse();

        _clock.Advance(CancelBudget + Grace);
        await Should.ThrowAsync<OperationCanceledException>(() => cancel).WaitAsync(Bound);
        _refresher.Calls.ShouldBe(0);
    }

    // ── The cut is a downgrade signal only when the agent says so ────────────

    // A healthy V2 agent whose cancel answers after the V2 share (12s of the 25s) must still be cancelled over V2: the share only decides WHEN to ask the agent,
    // and an agent that still advertises V2 is waited on, on the SAME call, until the one deadline. No second V2 call, no V1.
    [Fact]
    public async Task WhenTheV2ShareRunsOut_ButTheAgentStillAdvertisesV2_TheSameV2CancelIsAwaitedToTheOneDeadline_AndItsAnswerWins()
    {
        _refresher.AgentAnswer = V2Agent();

        var cancel = StartCancel(withFallback: true);

        _clock.Advance(DefaultV2Share);
        await _refresher.Started.WaitAsync(Bound);

        _clock.Advance(TimeSpan.FromSeconds(2));
        cancel.IsCompleted.ShouldBeFalse("the agent still speaks V2: its cancel is still the one to wait for");

        _v2Hangs.SetResult(Status());

        (await cancel.WaitAsync(Bound)).Version.ShouldBe(ScriptProtocolVersion.V2);
        _v1Cancels.ShouldBe(0);
        _v2.Verify(s => s.CancelAsync(It.IsAny<ScriptTicket>()), Times.Once());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenTheV2ShareRunsOut_AndTheAgentStillAdvertisesV2OrDoesNotAnswer_TheCancelEndsAtTheOneDeadline_WithoutV1(bool agentAnswers)
    {
        _refresher.AgentAnswer = agentAnswers ? V2Agent() : null;

        var cancel = StartCancel(withFallback: true);

        _clock.Advance(DefaultV2Share);
        await _refresher.Started.WaitAsync(Bound);

        _clock.Advance(CancelBudget + Grace - DefaultV2Share - Ms);
        cancel.IsCompleted.ShouldBeFalse("nothing licenses ending the V2 cancel before the one deadline");

        _clock.Advance(Ms);
        await Should.ThrowAsync<OperationCanceledException>(() => cancel).WaitAsync(Bound);
        _v1Cancels.ShouldBe(0);
        _v2.Verify(s => s.CancelAsync(It.IsAny<ScriptTicket>()), Times.Once());
    }

    // After the agent withdrew V2 the V2 cancel is left behind for the V1 one; when it fails later, nothing may surface as an unobserved task exception.
    [Fact]
    public async Task AV2CancelLeftBehindForV1_ThatFailsLater_HasItsFailureObserved()
    {
        var failure = new HalibutClientException($"late V2 cancel failure {Guid.NewGuid():N}");
        var unobserved = 0;

        void OnUnobserved(object sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => ReferenceEquals(x, failure))) Interlocked.Increment(ref unobserved);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;

        try
        {
            await CancelOverV1ThenFailTheV2CancelLeftBehindAsync(failure);

            CollectAbandonedTasks();

            Volatile.Read(ref unobserved).ShouldBe(0, "the V2 cancel left behind must have its failure observed");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task ACallerCancellation_IsNotADowngradeSignal_NeverRefreshes()
    {
        using var caller = new CancellationTokenSource();

        var cancel = ProtocolDowngrade.CancelAsync(_v2.Object, Context(withFallback: true), Target(), caller.Token);

        caller.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => cancel).WaitAsync(Bound);
        _refresher.Calls.ShouldBe(0);
        _v1Cancels.ShouldBe(0);
    }

    // ── The real chain: the mid-run helm rollback ────────────────────────────

    // Transport errors (the agent is mid-rollback) for the whole V2 budget; the capabilities answer then takes 9s of the 10s it may. Uncapped, V2 alone burns 20s of the 25s
    // and the refresh dies at the deadline: the script keeps running. Capped, V2 is cut at 10s, the refresh ends at 19s and V1 cancels with 6s to spare.
    [Fact]
    public async Task AfterAHelmRollback_TransientV2Errors_StillLeaveRoomForTheRefreshAndTheV1Cancel_ThroughTheCanceller()
    {
        var events = new List<string>();
        var v2 = new FakeV2Agent(events) { CancelFailure = new HalibutClientException("connection reset by peer") };
        var v1 = new FakeV1Agent(events);
        var refresher = new VirtualRefresher(_clock) { AgentAnswer = V1Only(), Delay = TimeSpan.FromSeconds(9), RefreshTimeout = RefreshTimeout };
        var canceller = Canceller(new FakeClientFactories(v1, v2), refresher);

        var cancel = canceller.CancelAllAsync(7);

        await WaitUntilAsync(() => _clock.ActiveTimers >= 3);
        _clock.Advance(DefaultV2Share);

        await refresher.Started.WaitAsync(Bound);
        _clock.Advance(TimeSpan.FromSeconds(9));

        await cancel.WaitAsync(Bound);

        events.ShouldContain("V1.Cancel:ticket-1", "the script must be cancelled over V1 once the agent is found to have withdrawn V2");
        _clock.Elapsed.ShouldBeLessThan(CancelBudget + Grace);
    }

    // ── The refresher advertises its own bound ───────────────────────────────

    [Fact]
    public void TheRealRefresher_ReportsItsConfiguredRefreshTimeout()
    {
        var setting = new Squid.Core.Settings.Halibut.HalibutSetting { Capabilities = new Squid.Core.Settings.Halibut.CapabilitiesSettings { RefreshTimeoutSeconds = 7 } };
        var refresher = new MachineCapabilitiesRefresher(null, new InMemoryMachineRuntimeCapabilitiesCache(), null, null, setting);

        refresher.RefreshTimeout.ShouldBe(TimeSpan.FromSeconds(7));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<CancelOutcome> StartCancel(bool withFallback, TimeSpan? budget = null, TimeSpan? grace = null)
        => ProtocolDowngrade.CancelAsync(_v2.Object, Context(withFallback, budget, grace), Target(), CancellationToken.None);

    private ScriptSessionContext Context(bool withFallback, TimeSpan? budget = null, TimeSpan? grace = null)
    {
        var retry = new RpcRetryHandler(CancelBudget, CancelBudget, budget ?? CancelBudget);
        // Without a refresher no fall-back can follow: the fall-back record still carries the clock the deadline runs on.
        var downgrade = new CancelDowngradeFallback(_selector.Object, withFallback ? _refresher : null, grace ?? Grace, _clock);

        return new ScriptSessionContext(retry, budget ?? CancelBudget, CancellationToken.None, null, downgrade);
    }

    private static CancelTarget Target() => new(1, new ServiceEndPoint("poll://sub-1/", "AABB", global::Halibut.Diagnostics.HalibutTimeoutsAndLimits.RecommendedValues()), new ScriptTicket("ticket-1"));

    private HalibutInFlightScriptCanceller Canceller(FakeClientFactories factories, VirtualRefresher refresher)
    {
        var store = new Mock<IInFlightScriptStore>();
        store.Setup(s => s.ListAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new InFlightScript(new DispatchSlot(1, 10, 100), "ticket-1", ScriptProtocolVersion.V2) });

        var machines = new Mock<IMachineDataProvider>();
        machines.Setup(m => m.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new Machine { Id = 1, Name = "agent-1", Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-cancel","Thumbprint":"AABBCCDD"}""" });

        var selector = new ScriptDispatcherSelector(new IScriptDispatcher[] { new V1ScriptDispatcher(factories), new V2ScriptDispatcher(factories) });
        var retry = new RpcRetryHandler(CancelBudget, CancelBudget, CancelBudget, () => _clock.GetUtcNow(), (d, ct) => Task.Delay(d, _clock, ct));

        return new HalibutInFlightScriptCanceller(store.Object, machines.Object, selector, new HalibutScriptObserver(), retry, Grace, refresher, _clock);
    }

    // Its own session and answer, referenced by nothing once it returns, so the V2 cancel task can be collected and its finalizer can report an unobserved failure.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task CancelOverV1ThenFailTheV2CancelLeftBehindAsync(Exception failure)
    {
        _refresher.AgentAnswer = V1Only();

        var v2Answer = new TaskCompletionSource<AgentScriptStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var v2 = new Mock<IScriptAgentSession>();
        v2.SetupGet(s => s.Version).Returns(ScriptProtocolVersion.V2);
        v2.Setup(s => s.CancelAsync(It.IsAny<ScriptTicket>())).Returns(v2Answer.Task);

        var cancel = ProtocolDowngrade.CancelAsync(v2.Object, Context(withFallback: true), Target(), CancellationToken.None);

        _clock.Advance(DefaultV2Share);
        await _refresher.Started.WaitAsync(Bound);

        (await cancel.WaitAsync(Bound)).Version.ShouldBe(ScriptProtocolVersion.V1);

        v2Answer.SetException(failure);
    }

    private static void CollectAbandonedTasks()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var bound = new CancellationTokenSource(Bound);

        while (!condition())
            await Task.Delay(1, bound.Token);
    }

    private static AgentScriptStatus Status() => new(new ScriptTicket("ticket-1"), AgentScriptState.Completed, ScriptExitCodes.Canceled, new List<ProcessOutput>(), 0);

    private static MachineRuntimeCapabilities V1Only() => new() { SupportedServices = new[] { "IScriptService/v1" } };

    private static MachineRuntimeCapabilities V2Agent() => new() { SupportedServices = new[] { "IScriptService/v1", ScriptServiceCapabilities.V2 } };

    /// <summary>A refresher that answers after a virtual delay (or hangs until its token is cancelled) and says when it was reached.</summary>
    private sealed class VirtualRefresher : IMachineCapabilitiesRefresher
    {
        private readonly VirtualClock _clock;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private CancellationToken _token;

        public VirtualRefresher(VirtualClock clock) => _clock = clock;

        public MachineRuntimeCapabilities AgentAnswer { get; set; }
        public TimeSpan Delay { get; set; }
        public bool HangUntilCancelled { get; set; }
        public TimeSpan RefreshTimeout { get; set; }
        public int Calls => Volatile.Read(ref _calls);
        public Task Started => _started.Task;
        public bool TokenCancelled => _token.IsCancellationRequested;

        public Task SyncFromStoreAsync(int machineId, CancellationToken ct) => Task.CompletedTask;

        public async Task<MachineRuntimeCapabilities> RefreshFromAgentAsync(int machineId, ServiceEndPoint endpoint, CancellationToken ct)
        {
            _token = ct;
            Interlocked.Increment(ref _calls);
            _started.TrySetResult();

            if (HangUntilCancelled) await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            else if (Delay > TimeSpan.Zero) await Task.Delay(Delay, _clock, ct).ConfigureAwait(false);

            return AgentAnswer;
        }
    }
}

/// <summary>A clock that moves only when a test advances it; its timers fire in due order as time passes them.</summary>
internal sealed class VirtualClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<VirtualTimer> _timers = new();
    private readonly DateTimeOffset _start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _now;

    public VirtualClock() => _now = _start;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => GetUtcNow().Ticks;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public TimeSpan Elapsed => GetUtcNow() - _start;

    public int ActiveTimers
    {
        get
        {
            lock (_gate) return _timers.Count(t => t.Active);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new VirtualTimer(this, callback, state);

        lock (_gate) _timers.Add(timer);

        timer.Change(dueTime, period);

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = GetUtcNow() + by;

        while (true)
        {
            VirtualTimer next;

            lock (_gate)
            {
                next = _timers.Where(t => t.Active && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();

                if (next == null)
                {
                    _now = target;
                    return;
                }

                if (next.Due > _now) _now = next.Due;

                next.Active = false;
            }

            next.Fire();
        }
    }

    private sealed class VirtualTimer : ITimer
    {
        private readonly VirtualClock _owner;
        private readonly TimerCallback _callback;
        private readonly object _state;

        public VirtualTimer(VirtualClock owner, TimerCallback callback, object state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public bool Active { get; set; }
        public DateTimeOffset Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero) throw new NotSupportedException("The code under test only uses one-shot timers.");

            lock (_owner._gate)
            {
                Active = dueTime != Timeout.InfiniteTimeSpan;
                Due = _owner._now + (Active ? dueTime : TimeSpan.Zero);
            }

            return true;
        }

        public void Fire() => _callback(_state);

        public void Dispose()
        {
            lock (_owner._gate)
            {
                Active = false;
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
