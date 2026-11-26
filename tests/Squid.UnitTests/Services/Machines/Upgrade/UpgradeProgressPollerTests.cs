using System.Linq.Expressions;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Models.Machines;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Drives the real <see cref="UpgradeProgressPoller"/> through a whole chain: each poll's probe (a fake health check)
/// writes the next agent-reported status into the real timeline store, and every poll the chain schedules is run next.
/// Deadlines are absolute, so "past the deadline" is arranged by date, never by sleeping.
/// </summary>
public sealed class UpgradeProgressPollerTests
{
    private const int MachineId = 42;

    private static readonly DateTimeOffset PreviousRun = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ThisRun = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUpgradeEventTimelineStore _store = new();
    private readonly Mock<IMachineHealthCheckService> _healthCheck = new();
    private readonly Mock<ISquidBackgroundJobClient> _jobs = new();
    private readonly List<(UpgradeProgressPoll Poll, TimeSpan Delay)> _scheduled = new();
    private readonly Queue<UpgradeStatusPayload> _agentStatuses = new();

    public UpgradeProgressPollerTests()
    {
        _healthCheck.Setup(h => h.ManualHealthCheckAsync(MachineId, It.IsAny<CancellationToken>())).Callback(() => { if (_agentStatuses.Count > 0) _store.StoreStatus(MachineId, _agentStatuses.Dequeue()); }).ReturnsAsync(new ManualHealthCheckResult());
        _jobs.Setup(j => j.Schedule<IUpgradeProgressPoller>(It.IsAny<Expression<Func<IUpgradeProgressPoller, Task>>>(), It.IsAny<TimeSpan>(), It.IsAny<string>())).Callback<Expression<Func<IUpgradeProgressPoller, Task>>, TimeSpan, string>((call, delay, _) => _scheduled.Add((ArgumentOf(call), delay))).Returns("job");
    }

    [Fact]
    public async Task LongUpgrade_IsPolledPastTheOldFixedWindow_UntilThisRunsTerminalStatus_ThenStops()
    {
        // A rollback: in flight far longer than the historical 120s window (its deadline is already behind us).
        for (var i = 0; i < 60; i++) _agentStatuses.Enqueue(Status(i < 30 ? "IN_PROGRESS" : "ROLLING_BACK", ThisRun));
        _agentStatuses.Enqueue(Status("ROLLED_BACK", ThisRun));

        var polls = await RunChainAsync(Poll(firstSignDeadline: Past, deadline: Future, baseline: PreviousRun));

        polls.ShouldBe(61, "every status up to and including the terminal one is observed, then the chain ends");
        _store.GetStatus(MachineId).Status.ShouldBe("ROLLED_BACK");
        _scheduled.ShouldAllBe(s => s.Delay == UpgradeProgressPoller.Interval);
    }

    [Fact]
    public async Task PreviousRunsTerminalStatus_IsNotMistakenForThisRuns()
    {
        _agentStatuses.Enqueue(Status("SUCCESS", PreviousRun));
        _agentStatuses.Enqueue(Status("IN_PROGRESS", ThisRun));
        _agentStatuses.Enqueue(Status("SUCCESS", ThisRun));

        var polls = await RunChainAsync(Poll(firstSignDeadline: Future, deadline: Future, baseline: PreviousRun));

        polls.ShouldBe(3, "stopping on the previous run's SUCCESS would freeze the UI before this run reports");
    }

    [Fact]
    public async Task ThisRunFailsBeforeTheFirstPoll_ItsTerminalStatusIsRecognisedByItsStartTime()
    {
        _agentStatuses.Enqueue(Status("FAILED", ThisRun));

        var polls = await RunChainAsync(Poll(firstSignDeadline: Future, deadline: Future, baseline: PreviousRun));

        polls.ShouldBe(1, "a later run's terminal status ends polling even when it was never seen in flight");
    }

    [Fact]
    public async Task TerminalStatusThatCannotBeToldApartFromThePreviousRun_KeepsPolling_UntilTheFirstSignDeadline()
    {
        _agentStatuses.Enqueue(Status("SUCCESS", ThisRun));

        await NewPoller().PollAsync(Poll(firstSignDeadline: Future, deadline: Future, baseline: null), CancellationToken.None);

        _scheduled.Count.ShouldBe(1, "with no baseline, a terminal status not seen in flight may be the previous run's");
    }

    [Fact]
    public async Task UnknownBaseline_TerminalStatusAfterTheRunWasSeenInFlight_Ends()
    {
        _agentStatuses.Enqueue(Status("IN_PROGRESS", ThisRun));
        _agentStatuses.Enqueue(Status("SUCCESS", ThisRun));

        var polls = await RunChainAsync(Poll(firstSignDeadline: Future, deadline: Future, baseline: null));

        polls.ShouldBe(2, "a cold status cache at dispatch must not keep polling once this run was seen through to its outcome");
    }

    [Fact]
    public async Task RunNeverSeenInFlight_StopsAtTheFirstSignDeadline_NotTheFullBudget()
    {
        // e.g. the strategy failed before the script ran: nothing ever changes on the agent.
        await NewPoller().PollAsync(Poll(firstSignDeadline: Past, deadline: Future, baseline: PreviousRun), CancellationToken.None);

        _scheduled.ShouldBeEmpty("polling may only outlive the historical window while an upgrade is actually running");
    }

    [Fact]
    public async Task StaleInFlightStatusOfThePreviousRun_DoesNotCountAsThisRun()
    {
        _agentStatuses.Enqueue(Status("IN_PROGRESS", PreviousRun));

        await NewPoller().PollAsync(Poll(firstSignDeadline: Past, deadline: Future, baseline: PreviousRun), CancellationToken.None);

        _scheduled.ShouldBeEmpty("an abandoned run's IN_PROGRESS is not a sign that this dispatch started anything");
    }

    [Fact]
    public async Task RunSeenInFlight_StopsAtTheBudgetDeadline()
    {
        _agentStatuses.Enqueue(Status("IN_PROGRESS", ThisRun));

        await NewPoller().PollAsync(Poll(firstSignDeadline: Past, deadline: Past, baseline: PreviousRun), CancellationToken.None);

        _scheduled.ShouldBeEmpty();
    }

    [Fact]
    public async Task SeenInFlight_IsCarriedToTheNextPoll()
    {
        _agentStatuses.Enqueue(Status("SWAPPED", ThisRun));

        await NewPoller().PollAsync(Poll(firstSignDeadline: Past, deadline: Future, baseline: PreviousRun), CancellationToken.None);

        _scheduled.ShouldHaveSingleItem().Poll.SawThisRun.ShouldBeTrue();
        _scheduled[0].Poll.MachineId.ShouldBe(MachineId);
        _scheduled[0].Poll.BaselineStartedAt.ShouldBe(PreviousRun);
    }

    [Fact]
    public async Task ProbeThatThrows_DoesNotEndTheChain()
    {
        _healthCheck.Setup(h => h.ManualHealthCheckAsync(MachineId, It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("agent restarting"));

        await NewPoller().PollAsync(Poll(firstSignDeadline: Future, deadline: Future, baseline: PreviousRun), CancellationToken.None);

        _scheduled.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ScheduleThatThrows_DoesNotFailThePoll_SoHangfireNeverRetriesItIntoASecondChain()
    {
        _jobs.Setup(j => j.Schedule<IUpgradeProgressPoller>(It.IsAny<Expression<Func<IUpgradeProgressPoller, Task>>>(), It.IsAny<TimeSpan>(), It.IsAny<string>())).Throws(new InvalidOperationException("redis down"));

        await Should.NotThrowAsync(() => NewPoller().PollAsync(Poll(firstSignDeadline: Future, deadline: Future, baseline: PreviousRun), CancellationToken.None));
    }

    [Fact]
    public async Task EveryPoll_ProbesTheMachine()
    {
        await NewPoller().PollAsync(Poll(firstSignDeadline: Future, deadline: Future, baseline: PreviousRun), CancellationToken.None);

        _healthCheck.Verify(h => h.ManualHealthCheckAsync(MachineId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Start_SchedulesTheFirstPoll_WithBothDeadlinesFromNow()
    {
        var before = DateTimeOffset.UtcNow;

        UpgradeProgressPoller.Start(_jobs.Object, MachineId, TimeSpan.FromMinutes(30), PreviousRun);

        var (poll, delay) = _scheduled.ShouldHaveSingleItem();
        delay.ShouldBe(UpgradeProgressPoller.Interval);
        poll.ShouldBe(poll with { MachineId = MachineId, BaselineStartedAt = PreviousRun, SawThisRun = false });
        poll.FirstSignDeadline.ShouldBeInRange(before + UpgradeProgressPoller.DefaultCeiling, DateTimeOffset.UtcNow + UpgradeProgressPoller.DefaultCeiling);
        poll.Deadline.ShouldBeInRange(before + TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void PollArgument_SurvivesTheHangfireJobArgumentRoundTrip()
    {
        // Same settings HangfireRegistrarBase gives Hangfire for job arguments. A poll that loses a field here would
        // silently restart the chain's state (deadlines, baseline, seen-in-flight) on every hop.
        var settings = new Newtonsoft.Json.JsonSerializerSettings { TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All };
        var poll = new UpgradeProgressPoll { MachineId = MachineId, FirstSignDeadline = ThisRun, Deadline = ThisRun.AddHours(2), BaselineStartedAt = PreviousRun, SawThisRun = true };

        var back = Newtonsoft.Json.JsonConvert.DeserializeObject(Newtonsoft.Json.JsonConvert.SerializeObject(poll, settings), settings);

        back.ShouldBe(poll);
    }

    [Fact]
    public void PollingCadenceAndDefaultWindow_ArePinned()
    {
        UpgradeProgressPoller.Interval.ShouldBe(TimeSpan.FromSeconds(3), "matches the UI's UPGRADE_EVENTS_POLL_INTERVAL_MS");
        UpgradeProgressPoller.DefaultCeiling.ShouldBe(TimeSpan.FromSeconds(120), "the historical window: still the budget for a run never seen in flight and for strategies without a script budget");
    }

    private static readonly DateTimeOffset Past = DateTimeOffset.UtcNow.AddMinutes(-1);
    private static readonly DateTimeOffset Future = DateTimeOffset.UtcNow.AddHours(6);

    private UpgradeProgressPoller NewPoller() => new(_healthCheck.Object, _store, _jobs.Object);

    /// <summary>Runs <paramref name="first"/> and then every poll the chain schedules; returns how many polls ran.</summary>
    private async Task<int> RunChainAsync(UpgradeProgressPoll first)
    {
        var poller = NewPoller();
        var polls = 0;
        var next = first;

        while (next != null && polls < 1000)
        {
            var scheduledBefore = _scheduled.Count;

            await poller.PollAsync(next, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            polls++;

            next = _scheduled.Count > scheduledBefore ? _scheduled[^1].Poll : null;
        }

        return polls;
    }

    private static UpgradeProgressPoll Poll(DateTimeOffset firstSignDeadline, DateTimeOffset deadline, DateTimeOffset? baseline) => new() { MachineId = MachineId, FirstSignDeadline = firstSignDeadline, Deadline = deadline, BaselineStartedAt = baseline };

    private static UpgradeStatusPayload Status(string status, DateTimeOffset startedAt) => new() { SchemaVersion = 2, Status = status, StartedAt = startedAt };

    /// <summary>The poll a scheduled <c>poller =&gt; poller.PollAsync(poll, ...)</c> job would run.</summary>
    private static UpgradeProgressPoll ArgumentOf(Expression<Func<IUpgradeProgressPoller, Task>> call)
    {
        var capture = new CapturingPoller();

        call.Compile()(capture);

        return capture.Poll;
    }

    private sealed class CapturingPoller : IUpgradeProgressPoller
    {
        public UpgradeProgressPoll Poll { get; private set; }

        public Task PollAsync(UpgradeProgressPoll poll, CancellationToken ct)
        {
            Poll = poll;
            return Task.CompletedTask;
        }
    }
}
