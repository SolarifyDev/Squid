using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Machines;
using Squid.Core.Settings.Halibut;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// <see cref="IInFlightScriptCanceller"/> for Tentacle agents: reads the task's recorded tickets and sends each one a CancelScript through the SAME
/// dispatcher (and so retry rules and wire version) the strategy used to start it — the version recorded with the ticket wins over whatever the
/// agent advertises now, because a script started in one dialect can only be addressed in that dialect.
/// </summary>
public sealed class HalibutInFlightScriptCanceller : IInFlightScriptCanceller
{
    /// <summary>Slack on top of the cancel retry budget before one agent's cancel is abandoned, so a call that ignores its budget cannot block a cancel.</summary>
    internal static readonly TimeSpan DefaultCancelGrace = TimeSpan.FromSeconds(5);

    private readonly IInFlightScriptStore _store;
    private readonly IMachineDataProvider _machines;
    private readonly IScriptDispatcherSelector _selector;
    private readonly IHalibutScriptObserver _observer;
    private readonly RpcRetryHandler _retry;
    private readonly TimeSpan _grace;

    public HalibutInFlightScriptCanceller(IInFlightScriptStore store, IMachineDataProvider machines, IScriptDispatcherSelector selector, IHalibutScriptObserver observer, HalibutSetting halibutSetting) : this(store, machines, selector, observer, new RpcRetryHandler(halibutSetting.RpcRetry))
    {
    }

    internal HalibutInFlightScriptCanceller(IInFlightScriptStore store, IMachineDataProvider machines, IScriptDispatcherSelector selector, IHalibutScriptObserver observer, RpcRetryHandler retry, TimeSpan? grace = null)
    {
        _store = store;
        _machines = machines;
        _selector = selector;
        _observer = observer;
        _retry = retry;
        _grace = grace ?? DefaultCancelGrace;
    }

    public async Task CancelAllAsync(int serverTaskId, CancellationToken ct = default)
    {
        var scripts = await ListScriptsAsync(serverTaskId, ct).ConfigureAwait(false);

        if (scripts.Count == 0) return;

        Log.Information("[Cancel] Task {TaskId} has {Count} in-flight agent script(s); cancelling them before its checkpoint is released", serverTaskId, scripts.Count);

        await Task.WhenAll(scripts.Select(script => CancelOneAsync(serverTaskId, script, ct))).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<InFlightScript>> ListScriptsAsync(int serverTaskId, CancellationToken ct)
    {
        try
        {
            return await _store.ListAsync(serverTaskId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning(ex, "[Cancel] Could not read the in-flight scripts of task {TaskId}; their agent scripts are not cancelled", serverTaskId);

            return Array.Empty<InFlightScript>();
        }
    }

    // One agent failing must not stop the others, nor the cancel itself.
    private async Task CancelOneAsync(int serverTaskId, InFlightScript script, CancellationToken ct)
    {
        try
        {
            await CancelOnAgentAsync(serverTaskId, script, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning(ex, "[Cancel] Could not cancel script {Ticket} on machine {MachineId} for task {TaskId}; it may keep running on the agent", script.Ticket, script.Slot.MachineId, serverTaskId);
        }
    }

    private async Task CancelOnAgentAsync(int serverTaskId, InFlightScript script, CancellationToken ct)
    {
        if (script.Protocol == null)
        {
            Log.Warning("[Cancel] Script {Ticket} of task {TaskId} was dispatched with a protocol this server does not know; not cancelling it", script.Ticket, serverTaskId);
            return;
        }

        var endpoint = await ResolveEndpointAsync(script, ct).ConfigureAwait(false);

        if (endpoint == null) return;

        var context = new ScriptSessionContext(_retry, _retry.CancelBudget, CancellationToken.None, _observer);
        var session = _selector.ForVersion(script.Protocol.Value).Open(endpoint, context);

        await session.CancelAsync(new ScriptTicket(script.Ticket)).WaitAsync(_retry.CancelBudget + _grace, ct).ConfigureAwait(false);

        Log.Information("[Cancel] Cancelled script {Ticket} (protocol {Protocol}) on machine {MachineId} for task {TaskId}", script.Ticket, script.Protocol, script.Slot.MachineId, serverTaskId);
    }

    private async Task<ServiceEndPoint> ResolveEndpointAsync(InFlightScript script, CancellationToken ct)
    {
        var machine = await _machines.GetMachinesByIdAsync(script.Slot.MachineId, ct).ConfigureAwait(false);

        if (machine == null)
        {
            Log.Warning("[Cancel] Machine {MachineId} no longer exists; script {Ticket} cannot be cancelled", script.Slot.MachineId, script.Ticket);
            return null;
        }

        return HalibutMachineExecutionStrategy.ParseMachineEndpoint(machine);
    }
}
