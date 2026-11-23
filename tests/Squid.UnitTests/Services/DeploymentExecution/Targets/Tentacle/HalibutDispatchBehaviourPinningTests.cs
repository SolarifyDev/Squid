using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Settings.Halibut;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// Behaviour pins for the Tentacle script dispatch path: what reaches the agent (every
/// <see cref="StartScriptCommand"/> field), the exact order of agent RPCs and in-flight store calls for
/// fresh dispatch / re-attach hit / re-attach miss / failures / cancellation, and the breaker accounting.
/// They drive the real <see cref="HalibutMachineExecutionStrategy"/> against hand-written fakes, so the
/// strategy can be re-factored (protocol-specific part behind a seam) without any of this changing.
/// Distinct values per same-typed field (machine 42 / step 11 / action 22 / task 777) make a swapped
/// argument observable.
/// </summary>
[Collection("HalibutPollingWorkAdmissionStaticState")]
public class HalibutDispatchBehaviourPinningTests
{
    private const int MachineId = 42;
    private const int StepId = 11;
    private const int ActionId = 22;
    private const int ServerTaskId = 777;

    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(7);

    private readonly List<string> _events = new();
    private readonly FakeAgent _agent;
    private readonly FakeStore _store;
    private readonly Mock<IHalibutClientFactory> _factory = new();
    private readonly MachineCircuitBreakerRegistry _breakers = new(new HalibutSetting());
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Machine _machine = new() { Id = MachineId, Name = "agent-42", Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-pin","Thumbprint":"AABBCCDD"}""" };

    public HalibutDispatchBehaviourPinningTests()
    {
        _agent = new FakeAgent(_events);
        _store = new FakeStore(_events);
        _factory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>())).Returns(_agent);
    }

    // ── Fresh dispatch ───────────────────────────────────────────────────────

    [Fact]
    public async Task FreshDispatch_Direct_SendsExactCommand_InExactOrder()
    {
        var result = await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        var cmd = _agent.StartCommands.ShouldHaveSingleItem();
        var fresh = cmd.ScriptTicket.TaskId;

        cmd.ScriptTicket.TaskId.ShouldMatch("^[a-f0-9]{32}$");
        cmd.ScriptBody.ShouldBe("echo pinned");
        cmd.Isolation.ShouldBe(ScriptIsolationLevel.FullIsolation);
        cmd.ScriptIsolationMutexTimeout.ShouldBe(ScriptTimeout, "arg 4 carries the script timeout");
        cmd.IsolationMutexName.ShouldBe("squid/machine/42", "arg 5 carries the machine-scoped mutex name");
        cmd.Arguments.ShouldBeEmpty();
        cmd.TaskId.ShouldBe("777", "arg 7 carries the server task id for correlation");
        cmd.DurationToWaitForScriptToFinish.ShouldBe(TimeSpan.Zero, "arg 8 is always zero");
        cmd.ScriptSyntax.ShouldBe(ScriptType.Python);
        cmd.TargetNamespace.ShouldBe("ns-pinned");
        cmd.Labels.ShouldBeNull();
        cmd.Files.Select(f => f.Name).ToArray().ShouldBe(new[] { "variables.json", "sensitiveVariables.json" });

        Normalised(fresh).ShouldBe(new[]
        {
            "Store.TryGet:42/11/22",
            "Store.Record:42/11/22:FRESH",
            "Agent.Start:FRESH",
            "Agent.GetStatus:FRESH:0",
            "Agent.GetStatus:FRESH:0",
            "Agent.Complete:FRESH:0",
            "Store.Clear:42/11/22"
        });
    }

    [Fact]
    public async Task FreshDispatch_Calamari_SendsPackagedFiles_AndSameCommandShape()
    {
        var request = DirectRequest();
        request.ExecutionMode = ExecutionMode.PackagedPayload;
        request.CalamariCommand = "calamari-run-script";

        await Strategy().ExecuteScriptAsync(request, CancellationToken.None);

        var cmd = _agent.StartCommands.ShouldHaveSingleItem();

        cmd.ScriptBody.ShouldContain("./variables.json");
        cmd.Isolation.ShouldBe(ScriptIsolationLevel.FullIsolation);
        cmd.ScriptIsolationMutexTimeout.ShouldBe(ScriptTimeout);
        cmd.IsolationMutexName.ShouldBe("squid/machine/42");
        cmd.Arguments.ShouldBeEmpty();
        cmd.TaskId.ShouldBe("777");
        cmd.DurationToWaitForScriptToFinish.ShouldBe(TimeSpan.Zero);
        cmd.ScriptSyntax.ShouldBe(ScriptType.Python);
        cmd.TargetNamespace.ShouldBe("ns-pinned");
        cmd.Files.Count.ShouldBe(3);
        cmd.Files[1].Name.ShouldBe("variables.json");
        cmd.Files[2].Name.ShouldBe("sensitiveVariables.json");
        cmd.Files[2].EncryptionPassword.ShouldNotBeNullOrEmpty();
        _events.First(e => e.StartsWith("Store.Record")).ShouldBe($"Store.Record:42/11/22:{cmd.ScriptTicket.TaskId}");
    }

    [Fact]
    public async Task FreshDispatch_WithoutRequestTimeout_UsesPollingDefaultTimeout()
    {
        var request = DirectRequest();
        request.Timeout = null;

        await Strategy().ExecuteScriptAsync(request, CancellationToken.None);

        _agent.StartCommands.Single().ScriptIsolationMutexTimeout.ShouldBe(TimeSpan.FromMinutes(Math.Max(1, new HalibutSetting().Polling.ScriptTimeoutMinutes)));
    }

    [Fact]
    public async Task FreshDispatch_RecordsTicketBeforeStartScript()
    {
        await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        var record = _events.FindIndex(e => e.StartsWith("Store.Record"));
        var start = _events.FindIndex(e => e.StartsWith("Agent.Start"));

        record.ShouldBeGreaterThanOrEqualTo(0);
        start.ShouldBeGreaterThan(record, "the in-flight pointer must be durable BEFORE the agent launches the script");
    }

    [Fact]
    public async Task FreshDispatch_HandsObserverTheDispatchContext()
    {
        var observer = new Mock<IHalibutScriptObserver>();
        var masker = new SensitiveValueMasker(new List<string>());
        ScriptTicket seenTicket = null;
        ScriptStatusResponse seenInitial = null;
        TimeSpan seenTimeout = default;
        ServiceEndPoint seenEndpoint = null;
        ScriptOutputSink seenSink = null;
        RemoteScriptCancelDecision seenDecision = null;
        SensitiveValueMasker seenMasker = null;
        Machine seenMachine = null;

        observer.Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptOutputSink>(), It.IsAny<RemoteScriptCancelDecision>()))
            .Callback<Machine, IAsyncScriptService, ScriptTicket, TimeSpan, CancellationToken, SensitiveValueMasker, ScriptStatusResponse, ServiceEndPoint, ScriptOutputSink, RemoteScriptCancelDecision>((m, _, t, to, _, mk, init, ep, sink, dec) => { seenMachine = m; seenTicket = t; seenTimeout = to; seenMasker = mk; seenInitial = init; seenEndpoint = ep; seenSink = sink; seenDecision = dec; })
            .ReturnsAsync(new ScriptExecutionResult { Success = true });

        ScriptOutputSink sink = (_, _) => Task.CompletedTask;
        RemoteScriptCancelDecision decision = () => true;
        var request = DirectRequest();
        request.Masker = masker;
        request.OutputSink = sink;
        request.ShouldCancelRemoteScript = decision;

        await Strategy(observer: observer.Object).ExecuteScriptAsync(request, CancellationToken.None);

        var cmd = _agent.StartCommands.Single();

        seenMachine.ShouldBeSameAs(_machine);
        seenTicket.TaskId.ShouldBe(cmd.ScriptTicket.TaskId);
        seenTimeout.ShouldBe(ScriptTimeout);
        seenMasker.ShouldBeSameAs(masker);
        seenInitial.ShouldNotBeNull();
        seenInitial.State.ShouldBe(ProcessState.Running, "the StartScript response is the observer's initial status");
        seenEndpoint.BaseUri.ToString().ShouldBe("poll://sub-pin/");
        seenSink.ShouldBeSameAs(sink);
        seenDecision.ShouldBeSameAs(decision);
        _events.ShouldContain(e => e == "Store.Clear:42/11/22");
    }

    [Fact]
    public async Task UnsupportedSyntax_ThrowsBeforeAnyStoreOrStartRpc()
    {
        var request = DirectRequest();
        request.Syntax = (ScriptSyntax)999;

        await Should.ThrowAsync<InvalidOperationException>(() => Strategy().ExecuteScriptAsync(request, CancellationToken.None));

        _events.ShouldBeEmpty("the command is built before the re-attach probe, the in-flight record and StartScript — a bad syntax must not leave a pointer behind");
    }

    // ── Re-attach ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reattach_Hit_ObservesRecordedTicket_NoDuplicateDispatch()
    {
        _store.Seed(new DispatchSlot(MachineId, StepId, ActionId), "OLD");
        _agent.Know("OLD");

        var result = await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        _agent.StartCommands.ShouldBeEmpty("a still-running recorded script must never be dispatched twice");
        _events.ShouldBe(new[]
        {
            "Store.TryGet:42/11/22",
            "Agent.GetStatus:OLD:0",
            "Agent.GetStatus:OLD:0",
            "Agent.Complete:OLD:0",
            "Store.Clear:42/11/22"
        });
    }

    [Fact]
    public async Task Reattach_Miss_UnknownTicketSentinel_ClearsThenDispatchesFresh()
    {
        _store.Seed(new DispatchSlot(MachineId, StepId, ActionId), "OLD");

        await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        var fresh = _agent.StartCommands.ShouldHaveSingleItem().ScriptTicket.TaskId;

        fresh.ShouldNotBe("OLD");
        Normalised(fresh).ShouldBe(new[]
        {
            "Store.TryGet:42/11/22",
            "Agent.GetStatus:OLD:0",
            "Store.Clear:42/11/22",
            "Store.Record:42/11/22:FRESH",
            "Agent.Start:FRESH",
            "Agent.GetStatus:FRESH:0",
            "Agent.GetStatus:FRESH:0",
            "Agent.Complete:FRESH:0",
            "Store.Clear:42/11/22"
        });
    }

    [Fact]
    public async Task Reattach_ProbeNonTransientFailure_ClearsThenDispatchesFresh()
    {
        _store.Seed(new DispatchSlot(MachineId, StepId, ActionId), "OLD");
        _agent.ProbeFailure = new InvalidOperationException("agent exploded");

        await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        var fresh = _agent.StartCommands.ShouldHaveSingleItem().ScriptTicket.TaskId;

        Normalised(fresh).Take(5).ShouldBe(new[]
        {
            "Store.TryGet:42/11/22",
            "Agent.GetStatus:OLD:0",
            "Store.Clear:42/11/22",
            "Store.Record:42/11/22:FRESH",
            "Agent.Start:FRESH"
        });
    }

    [Fact]
    public async Task Reattach_ProbeTransientFailure_PausesAndPreservesPointer()
    {
        _store.Seed(new DispatchSlot(MachineId, StepId, ActionId), "OLD");
        _agent.ProbeFailure = new HalibutClientException("connection reset");

        await Should.ThrowAsync<HalibutClientException>(() => Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None));

        _agent.StartCommands.ShouldBeEmpty("a transient probe failure must not dispatch fresh — the old script may still run");
        _events.ShouldNotContain(e => e.StartsWith("Store.Clear") || e.StartsWith("Store.Record"));
        _breakers.GetOrCreate(MachineId).ConsecutiveFailures.ShouldBe(1);
    }

    // ── Failure / cancellation ───────────────────────────────────────────────

    [Fact]
    public async Task TransientFailureDuringObservation_PreservesPointer_AndChargesBreakerOnce()
    {
        _agent.StatusFailure = new HalibutClientException("connection reset");

        await Should.ThrowAsync<HalibutClientException>(() => Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None));

        _events.ShouldContain(e => e.StartsWith("Store.Record"));
        _events.ShouldContain(e => e.StartsWith("Agent.Start"));
        _events.ShouldNotContain(e => e.StartsWith("Store.Clear"), "a throw leaves the pointer so a resume re-attaches");
        _events.ShouldNotContain(e => e.StartsWith("Agent.Complete"));
        _breakers.GetOrCreate(MachineId).ConsecutiveFailures.ShouldBe(1);
    }

    [Fact]
    public async Task SuccessfulDispatch_ResetsBreakerFailures()
    {
        var breaker = _breakers.GetOrCreate(MachineId);
        breaker.RecordFailure();

        await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        breaker.ConsecutiveFailures.ShouldBe(0);
    }

    [Theory]
    [InlineData(false, true)]   // no decision: legacy behaviour, cancel on every interruption
    [InlineData(true, true)]    // decision says the outcome is final: cancel the remote script
    [InlineData(true, false)]   // decision says the task is only pausing: leave it running
    public async Task CancellationDuringObservation_CancelsRemoteScriptPerDecision_PreservesPointer_NoBreakerFailure(bool hasDecision, bool decisionValue)
    {
        using var cts = new CancellationTokenSource();
        _agent.OnGetStatus = () => cts.Cancel();

        var request = DirectRequest();
        request.ShouldCancelRemoteScript = hasDecision ? () => decisionValue : null;

        await Should.ThrowAsync<OperationCanceledException>(() => Strategy().ExecuteScriptAsync(request, cts.Token));

        var cancelled = _events.Any(e => e.StartsWith("Agent.Cancel"));

        cancelled.ShouldBe(!hasDecision || decisionValue);
        _events.ShouldNotContain(e => e.StartsWith("Store.Clear"), "cancellation throws, so the pointer survives");
        _breakers.GetOrCreate(MachineId).ConsecutiveFailures.ShouldBe(0, "cancellation is not an agent failure");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private HalibutMachineExecutionStrategy Strategy(IHalibutScriptObserver observer = null)
    {
        var retry = new RpcRetryHandler(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), () => _now, (d, _) => { _now += d; return Task.CompletedTask; });
        observer ??= new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 1, MaxPollIntervalMs = 2 });

        return new HalibutMachineExecutionStrategy(_factory.Object, new CalamariPayloadBuilder(new Mock<IYamlNuGetPacker>().Object), observer, new HalibutSetting(), _breakers, inFlightStore: _store, rpcRetry: retry);
    }

    private ScriptExecutionRequest DirectRequest() => new()
    {
        Machine = _machine,
        ScriptBody = "echo pinned",
        ExecutionMode = ExecutionMode.DirectScript,
        Syntax = ScriptSyntax.Python,
        TargetNamespace = "ns-pinned",
        ReleaseVersion = "1.0.0",
        ServerTaskId = ServerTaskId,
        StepId = StepId,
        ActionId = ActionId,
        StepName = "step",
        ActionName = "action",
        Timeout = ScriptTimeout,
        Variables = new List<Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "Plain", Value = "v", IsSensitive = false },
            new() { Name = "Secret", Value = "s", IsSensitive = true }
        }
    };

    private string[] Normalised(string freshTicket) => _events.Select(e => e.Replace(freshTicket, "FRESH")).ToArray();

    private sealed class FakeStore : IInFlightScriptStore
    {
        private readonly List<string> _events;
        private readonly Dictionary<DispatchSlot, string> _tickets = new();

        public FakeStore(List<string> events) => _events = events;

        public void Seed(DispatchSlot slot, string ticket) => _tickets[slot] = ticket;

        public Task RecordDispatchedAsync(int serverTaskId, DispatchSlot slot, string scriptTicket, CancellationToken cancellationToken = default)
        {
            serverTaskId.ShouldBe(ServerTaskId);
            _events.Add($"Store.Record:{Format(slot)}:{scriptTicket}");
            _tickets[slot] = scriptTicket;
            return Task.CompletedTask;
        }

        public Task ClearAsync(int serverTaskId, DispatchSlot slot, CancellationToken cancellationToken = default)
        {
            serverTaskId.ShouldBe(ServerTaskId);
            _events.Add($"Store.Clear:{Format(slot)}");
            _tickets.Remove(slot);
            return Task.CompletedTask;
        }

        public Task<string> TryGetTicketAsync(int serverTaskId, DispatchSlot slot, CancellationToken cancellationToken = default)
        {
            serverTaskId.ShouldBe(ServerTaskId);
            _events.Add($"Store.TryGet:{Format(slot)}");
            return Task.FromResult(_tickets.TryGetValue(slot, out var t) ? t : null);
        }

        public Task<bool> IsMachineBusyAsync(int machineId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        private static string Format(DispatchSlot s) => $"{s.MachineId}/{s.StepId}/{s.ActionId}";
    }

    /// <summary>Agent that knows only the tickets it was started with (or told about); unknown tickets answer Complete + UnknownResult like the real Tentacle.</summary>
    private sealed class FakeAgent : IAsyncScriptService
    {
        private readonly List<string> _events;
        private readonly HashSet<string> _known = new();
        private readonly Dictionary<string, int> _polls = new();

        public FakeAgent(List<string> events) => _events = events;

        public List<StartScriptCommand> StartCommands { get; } = new();
        public Exception ProbeFailure { get; set; }
        public Exception StatusFailure { get; set; }
        public Action OnGetStatus { get; set; }

        public void Know(string ticket) => _known.Add(ticket);

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
        {
            _events.Add($"Agent.Start:{command.ScriptTicket.TaskId}");
            StartCommands.Add(command);
            _known.Add(command.ScriptTicket.TaskId);

            return Task.FromResult(new ScriptStatusResponse(command.ScriptTicket, ProcessState.Running, 0, new List<ProcessOutput>(), 0));
        }

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
        {
            var id = request.Ticket.TaskId;
            _events.Add($"Agent.GetStatus:{id}:{request.LastLogSequence}");

            _polls[id] = _polls.GetValueOrDefault(id) + 1;

            if (ProbeFailure != null && !StartCommands.Any(c => c.ScriptTicket.TaskId == id)) throw ProbeFailure;
            if (StatusFailure != null) throw StatusFailure;

            OnGetStatus?.Invoke();

            if (!_known.Contains(id))
                return Task.FromResult(new ScriptStatusResponse(request.Ticket, ProcessState.Complete, ScriptExitCodes.UnknownResult, new List<ProcessOutput>(), 0));

            var state = _polls[id] >= 2 ? ProcessState.Complete : ProcessState.Running;

            return Task.FromResult(new ScriptStatusResponse(request.Ticket, state, 0, new List<ProcessOutput>(), 0));
        }

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
        {
            _events.Add($"Agent.Cancel:{command.Ticket.TaskId}");

            return Task.FromResult(new ScriptStatusResponse(command.Ticket, ProcessState.Complete, ScriptExitCodes.Canceled, new List<ProcessOutput>(), 0));
        }

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command)
        {
            _events.Add($"Agent.Complete:{command.Ticket.TaskId}:{command.LastLogSequence}");

            return Task.FromResult(new ScriptStatusResponse(command.Ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));
        }
    }
}
