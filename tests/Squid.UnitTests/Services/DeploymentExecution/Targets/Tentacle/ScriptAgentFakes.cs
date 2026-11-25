using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// Hand-written agents for the V1 / V2 dispatch tests. Both log every RPC into one shared, ordered event
/// list (with the in-flight store's events) so a test can assert the exact conversation. A V2 agent
/// answers in the V2 contract's explicit states; a V1 agent answers an unknown ticket the way the real
/// Tentacle does (Complete + UnknownResult).
/// </summary>
internal sealed class FakeV2Agent : IAsyncScriptServiceV2
{
    private readonly List<string> _events;

    public FakeV2Agent(List<string> events) => _events = events;

    public sealed class Script
    {
        public ScriptStateV2 State { get; set; } = ScriptStateV2.Running;
        public int ExitCode { get; set; }
        public int Polls { get; set; }
    }

    /// <summary>A running script reports Completed on this poll.</summary>
    public int PollsToComplete { get; set; } = 2;

    /// <summary>The exit code a script that this agent starts finishes with.</summary>
    public int StartedScriptExitCode { get; set; }

    public Dictionary<string, Script> Scripts { get; } = new();
    public Dictionary<string, (ScriptStateV2 State, int ExitCode)> Tombstones { get; } = new();
    public List<StartScriptCommandV2> StartCommands { get; } = new();
    public Queue<Exception> StartFailuresBeforeRunning { get; } = new();
    public Queue<Exception> StartFailuresAfterRunning { get; } = new();
    public Exception GetStatusFailure { get; set; }
    public Exception CancelFailure { get; set; }
    public Action OnGetStatus { get; set; }
    public int StartCalls { get; private set; }

    public void Know(string ticket, ScriptStateV2 state = ScriptStateV2.Running, int exitCode = 0) => Scripts[ticket] = new Script { State = state, ExitCode = exitCode };

    public Task<ScriptStatusResponseV2> StartScriptAsync(StartScriptCommandV2 command)
    {
        var id = command.ScriptTicket.TaskId;
        StartCalls++;
        _events.Add($"V2.Start:{id}");

        if (StartFailuresBeforeRunning.TryDequeue(out var beforeRunning)) throw beforeRunning;

        if (!Scripts.ContainsKey(id))
        {
            StartCommands.Add(command);
            Scripts[id] = new Script { ExitCode = StartedScriptExitCode };
        }

        if (StartFailuresAfterRunning.TryDequeue(out var afterRunning)) throw afterRunning;

        return Task.FromResult(Status(command.ScriptTicket, ScriptStateV2.Running, 0));
    }

    public Task<ScriptStatusResponseV2> GetStatusAsync(ScriptStatusRequestV2 request)
    {
        var id = request.Ticket.TaskId;
        _events.Add($"V2.GetStatus:{id}:{request.LastLogSequence}");

        if (GetStatusFailure != null) throw GetStatusFailure;

        OnGetStatus?.Invoke();

        if (!Scripts.TryGetValue(id, out var script))
        {
            // Like the real agent: a released ticket is answered from the completion tombstone.
            return Task.FromResult(Tombstones.TryGetValue(id, out var tomb) ? Status(request.Ticket, tomb.State, tomb.State == ScriptStateV2.Completed ? tomb.ExitCode : 0) : Status(request.Ticket, ScriptStateV2.UnknownTicket, 0));
        }

        script.Polls++;

        if (script.State == ScriptStateV2.Running && script.Polls >= PollsToComplete) script.State = ScriptStateV2.Completed;

        return Task.FromResult(Status(request.Ticket, script.State, script.State == ScriptStateV2.Completed ? script.ExitCode : 0));
    }

    public Task<ScriptStatusResponseV2> CancelScriptAsync(CancelScriptCommandV2 command)
    {
        var id = command.Ticket.TaskId;
        _events.Add($"V2.Cancel:{id}");

        if (CancelFailure != null) throw CancelFailure;

        if (Scripts.TryGetValue(id, out var script))
        {
            script.State = ScriptStateV2.Completed;
            script.ExitCode = ScriptExitCodes.Canceled;

            return Task.FromResult(Status(command.Ticket, ScriptStateV2.Completed, ScriptExitCodes.Canceled));
        }

        return Task.FromResult(Tombstones.TryGetValue(id, out var tomb) ? Status(command.Ticket, tomb.State, tomb.State == ScriptStateV2.Completed ? tomb.ExitCode : 0) : Status(command.Ticket, ScriptStateV2.UnknownTicket, 0));
    }

    public Task<CompleteScriptResponseV2> CompleteScriptAsync(CompleteScriptCommandV2 command)
    {
        var id = command.Ticket.TaskId;
        _events.Add($"V2.Complete:{id}:{command.LastLogSequence}");

        if (Scripts.TryGetValue(id, out var script))
        {
            if (script.State == ScriptStateV2.Running) return Task.FromResult(new CompleteScriptResponseV2(command.Ticket, CompleteScriptOutcomeV2.NotYetComplete, Status(command.Ticket, ScriptStateV2.Running, 0)));

            Scripts.Remove(id);
            Tombstones[id] = (script.State, script.ExitCode);

            return Task.FromResult(new CompleteScriptResponseV2(command.Ticket, CompleteScriptOutcomeV2.Completed, Status(command.Ticket, script.State, script.ExitCode)));
        }

        var outcome = Tombstones.ContainsKey(id) ? CompleteScriptOutcomeV2.AlreadyCompleted : CompleteScriptOutcomeV2.UnknownTicket;

        return Task.FromResult(new CompleteScriptResponseV2(command.Ticket, outcome, null));
    }

    private static ScriptStatusResponseV2 Status(ScriptTicket ticket, ScriptStateV2 state, int exitCode) => new(ticket, state, exitCode, new List<ProcessOutput>(), 0);
}

internal sealed class FakeV1Agent : IAsyncScriptService
{
    private readonly List<string> _events;
    private readonly HashSet<string> _known = new();
    private readonly Dictionary<string, int> _polls = new();

    public FakeV1Agent(List<string> events) => _events = events;

    public List<StartScriptCommand> StartCommands { get; } = new();
    public Queue<Exception> StartFailures { get; } = new();
    public int StartCalls { get; private set; }

    public void Know(string ticket) => _known.Add(ticket);

    public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
    {
        StartCalls++;
        _events.Add($"V1.Start:{command.ScriptTicket.TaskId}");

        if (StartFailures.TryDequeue(out var failure)) throw failure;

        StartCommands.Add(command);
        _known.Add(command.ScriptTicket.TaskId);

        return Task.FromResult(Resp(command.ScriptTicket, ProcessState.Running, 0));
    }

    public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
    {
        var id = request.Ticket.TaskId;
        _events.Add($"V1.GetStatus:{id}:{request.LastLogSequence}");
        _polls[id] = _polls.GetValueOrDefault(id) + 1;

        if (!_known.Contains(id)) return Task.FromResult(Resp(request.Ticket, ProcessState.Complete, ScriptExitCodes.UnknownResult));

        return Task.FromResult(Resp(request.Ticket, _polls[id] >= 2 ? ProcessState.Complete : ProcessState.Running, 0));
    }

    public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
    {
        _events.Add($"V1.Cancel:{command.Ticket.TaskId}");

        return Task.FromResult(Resp(command.Ticket, ProcessState.Complete, ScriptExitCodes.Canceled));
    }

    public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command)
    {
        _events.Add($"V1.Complete:{command.Ticket.TaskId}:{command.LastLogSequence}");

        return Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 0));
    }

    private static ScriptStatusResponse Resp(ScriptTicket ticket, ProcessState state, int exitCode) => new(ticket, state, exitCode, new List<ProcessOutput>(), 0);
}

/// <summary>Client factory that hands out the fakes and counts how many V2 clients were opened (a V1-only choice must never open one).</summary>
internal sealed class FakeClientFactories : IHalibutClientFactory, IScriptServiceV2ClientFactory
{
    private readonly IAsyncScriptService _v1;
    private readonly IAsyncScriptServiceV2 _v2;

    public FakeClientFactories(IAsyncScriptService v1, IAsyncScriptServiceV2 v2)
    {
        _v1 = v1;
        _v2 = v2;
    }

    public int V1Opened { get; private set; }
    public int V2Opened { get; private set; }

    public IAsyncScriptService CreateClient(ServiceEndPoint endpoint)
    {
        V1Opened++;
        return _v1;
    }

    public IAsyncScriptServiceV2 CreateScriptServiceV2Client(ServiceEndPoint endpoint)
    {
        V2Opened++;
        return _v2;
    }

    public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => throw new NotSupportedException();

    public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => throw new NotSupportedException();
}

/// <summary>In-flight store that records the protocol version it was given, like the real store does; a seeded record with no version is a legacy (V1) record.</summary>
internal sealed class VersionedFakeStore : IInFlightScriptStore
{
    private readonly List<string> _events;
    private readonly Dictionary<DispatchSlot, (string Ticket, ScriptProtocolVersion? Version)> _records = new();

    public VersionedFakeStore(List<string> events) => _events = events;

    public void Seed(DispatchSlot slot, string ticket, ScriptProtocolVersion? version) => _records[slot] = (ticket, version);

    public ScriptProtocolVersion? Recorded(DispatchSlot slot) => _records.TryGetValue(slot, out var r) ? r.Version : null;

    public Task RecordDispatchedAsync(int serverTaskId, DispatchSlot slot, string scriptTicket, CancellationToken cancellationToken = default)
        => RecordDispatchedAsync(serverTaskId, slot, scriptTicket, ScriptProtocolVersion.V1, cancellationToken);

    public Task RecordDispatchedAsync(int serverTaskId, DispatchSlot slot, string scriptTicket, ScriptProtocolVersion protocol, CancellationToken cancellationToken = default)
    {
        _events.Add($"Store.Record:{Format(slot)}:{scriptTicket}:{protocol}");
        _records[slot] = (scriptTicket, protocol);

        return Task.CompletedTask;
    }

    public Task ClearAsync(int serverTaskId, DispatchSlot slot, CancellationToken cancellationToken = default)
    {
        _events.Add($"Store.Clear:{Format(slot)}");
        _records.Remove(slot);

        return Task.CompletedTask;
    }

    public Task<string> TryGetTicketAsync(int serverTaskId, DispatchSlot slot, CancellationToken cancellationToken = default)
    {
        _events.Add($"Store.TryGet:{Format(slot)}");

        return Task.FromResult(_records.TryGetValue(slot, out var r) ? r.Ticket : null);
    }

    public Task<ScriptProtocolVersion?> TryGetProtocolAsync(int serverTaskId, DispatchSlot slot, CancellationToken cancellationToken = default)
        => Task.FromResult(_records.TryGetValue(slot, out var r) ? r.Version : null);

    public Task<bool> IsMachineBusyAsync(int machineId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    private static string Format(DispatchSlot s) => $"{s.MachineId}/{s.StepId}/{s.ActionId}";
}

/// <summary>
/// Capabilities refresher whose agent answer is scripted: a snapshot (the agent answered), or <see langword="null"/> (it did not).
/// Records every call so a test can pin that the refresh is only ever made for a failed V2 call.
/// </summary>
internal sealed class FakeCapabilitiesRefresher : Squid.Core.Services.DeploymentExecution.Tentacle.IMachineCapabilitiesRefresher
{
    private readonly List<string> _events;

    public FakeCapabilitiesRefresher(List<string> events) => _events = events;

    public Squid.Core.Services.DeploymentExecution.Tentacle.MachineRuntimeCapabilities AgentAnswer { get; set; }
    public int RefreshCalls { get; private set; }

    /// <summary>How long an agent capabilities call takes; like the real refresher it stops early when the caller's token is cancelled.</summary>
    public TimeSpan RefreshDelay { get; set; }

    public List<int> SyncedMachines { get; } = new();

    public Task SyncFromStoreAsync(int machineId, CancellationToken ct)
    {
        SyncedMachines.Add(machineId);
        _events.Add($"Caps.Sync:{machineId}");

        return Task.CompletedTask;
    }

    public async Task<Squid.Core.Services.DeploymentExecution.Tentacle.MachineRuntimeCapabilities> RefreshFromAgentAsync(int machineId, ServiceEndPoint endpoint, CancellationToken ct)
    {
        RefreshCalls++;
        _events.Add($"Caps.Refresh:{machineId}");

        if (RefreshDelay != TimeSpan.Zero) await Task.Delay(RefreshDelay, ct);

        return AgentAnswer;
    }
}

/// <summary>Hands the persistence straight through; the real <c>MachineCapabilitiesStoreAccess</c> opens a lifetime scope per call (covered by the integration tier).</summary>
internal sealed class DirectStoreAccess : Squid.Core.Services.DeploymentExecution.Tentacle.IMachineCapabilitiesStoreAccess
{
    private readonly Squid.Core.Services.DeploymentExecution.Tentacle.IMachineRuntimeCapabilitiesPersistence _persistence;

    public DirectStoreAccess(Squid.Core.Services.DeploymentExecution.Tentacle.IMachineRuntimeCapabilitiesPersistence persistence) => _persistence = persistence;

    public Task<T> RunAsync<T>(Func<Squid.Core.Services.DeploymentExecution.Tentacle.IMachineRuntimeCapabilitiesPersistence, Task<T>> operation) => operation(_persistence);
}
