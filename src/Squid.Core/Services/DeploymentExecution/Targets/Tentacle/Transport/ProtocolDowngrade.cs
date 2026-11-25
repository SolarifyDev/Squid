using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// The one definition of "this V2 call failed because the agent was downgraded to V1", shared by every path that addresses a script over V2 (start,
/// resume probe, cancel). A V1-only agent answers a V2 call with a plain transport error the classifier treats as a blip, so a failure alone proves
/// nothing: the agent is asked what it advertises, and only an answer that no longer lists V2 licenses the V1 fall-back.
/// </summary>
internal static class ProtocolDowngrade
{
    /// <summary>A V2 call that failed in a way a V1-only agent would also produce: a transport blip, or the service/method being absent.</summary>
    internal static bool IsPossibleMismatch(ScriptProtocolVersion version, Exception ex)
        => version == ScriptProtocolVersion.V2 && (TransientFailureClassifier.IsTransient(ex) || TransientFailureClassifier.IsServiceMissing(ex));

    /// <summary>True only when the agent ANSWERED a capabilities call and no longer lists V2; an unanswered call (or no refresher) proves nothing.</summary>
    internal static async Task<bool> AgentWithdrewV2Async(IMachineCapabilitiesRefresher refresher, int machineId, ServiceEndPoint endpoint, CancellationToken ct)
    {
        if (refresher == null) return false;

        var capabilities = await refresher.RefreshFromAgentAsync(machineId, endpoint, ct).ConfigureAwait(false);

        return capabilities != null && !capabilities.SupportsScriptServiceV2;
    }

    /// <summary>
    /// Cancels a script over the version it runs under — and, for the one cancel of one script, shares ONE deadline of cancel budget + grace across the
    /// V2 attempt, the capabilities refresh and the V1 retry (the caller may be an HTTP request). A V2 cancel that fails because the agent was downgraded
    /// since (a pod rollback keeps its script pods) is retried over V1: both dialects share one agent script state, so a V1 cancel of a V2 ticket is valid.
    /// Used by the in-flight canceller and by the observer's mid-run cancel, so both treat a downgraded agent alike. Any other failure propagates unchanged.
    /// </summary>
    internal static async Task<CancelOutcome> CancelAsync(IScriptAgentSession session, ScriptSessionContext context, CancelTarget target, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(context.Retry.CancelBudget + (context.Downgrade?.Grace ?? HalibutInFlightScriptCanceller.DefaultCancelGrace));

        try
        {
            return new CancelOutcome(session.Version, await session.CancelAsync(target.Ticket).WaitAsync(deadline.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (context.Downgrade != null && IsPossibleMismatch(session.Version, ex))
        {
            if (!await AgentWithdrewV2Async(context.Downgrade.Refresher, target.MachineId, target.Endpoint, deadline.Token).ConfigureAwait(false)) throw;

            Log.Warning(ex, "[Cancel] Agent of machine {MachineId} no longer advertises script service V2; cancelling V2 ticket {Ticket} over V1", target.MachineId, target.Ticket.TaskId);

            var v1 = context.Downgrade.Selector.ForVersion(ScriptProtocolVersion.V1).Open(target.Endpoint, context);

            return new CancelOutcome(v1.Version, await v1.CancelAsync(target.Ticket).WaitAsync(deadline.Token).ConfigureAwait(false));
        }
    }
}

/// <summary>The script a cancel addresses: its machine (for the capabilities check), the endpoint to reach it on and its ticket.</summary>
internal sealed record CancelTarget(int MachineId, ServiceEndPoint Endpoint, ScriptTicket Ticket);

/// <summary>The version that carried the cancel and the status the agent answered.</summary>
internal sealed record CancelOutcome(ScriptProtocolVersion Version, AgentScriptStatus Status);

/// <summary>
/// The V2 client the observer polls, with its cancel routed through <see cref="ProtocolDowngrade.CancelAsync"/>. Everything else, including the
/// stop-retrying binding the observer's liveness handling relies on, is the wrapped retrying client's.
/// </summary>
internal sealed class V1FallbackCancelClient : IScriptAgentClient, IStopRetryingAware
{
    private readonly IScriptAgentClient _inner;
    private readonly Func<ScriptTicket, Task<CancelOutcome>> _cancel;

    public V1FallbackCancelClient(IScriptAgentClient inner, Func<ScriptTicket, Task<CancelOutcome>> cancel)
    {
        _inner = inner;
        _cancel = cancel;
    }

    public void BindStopRetrying(CancellationToken stopRetrying)
    {
        if (_inner is IStopRetryingAware aware) aware.BindStopRetrying(stopRetrying);
    }

    public Task<AgentScriptStatus> GetStatusAsync(ScriptTicket ticket, long lastLogSequence) => _inner.GetStatusAsync(ticket, lastLogSequence);

    public async Task<AgentScriptStatus> CancelAsync(ScriptTicket ticket, long lastLogSequence) => (await _cancel(ticket).ConfigureAwait(false)).Status;

    public Task<AgentScriptCompletion> CompleteAsync(ScriptTicket ticket, long lastLogSequence) => _inner.CompleteAsync(ticket, lastLogSequence);
}
