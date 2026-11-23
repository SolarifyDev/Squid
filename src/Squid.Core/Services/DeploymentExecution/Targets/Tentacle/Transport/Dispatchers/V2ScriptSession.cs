using Halibut;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers;

/// <summary>
/// The V2 conversation with an agent. V2 states reach the observer unflattened; Start is idempotent per
/// ticket on the agent (a repeated Start returns the known status and never runs the script twice), so —
/// unlike V1 — it is retried inside the same time budget as GetStatus. A lost Start response is therefore
/// recovered by the retry instead of surfacing as a pause.
/// </summary>
internal sealed class V2ScriptSession : IScriptAgentSession
{
    private readonly V2ScriptAgentClient _raw;
    private readonly RetryingScriptAgentClient _retrying;
    private readonly ScriptSessionContext _context;

    public V2ScriptSession(V2ScriptAgentClient raw, RetryingScriptAgentClient retrying, ScriptSessionContext context)
    {
        _raw = raw;
        _retrying = retrying;
        _context = context;
    }

    public ScriptProtocolVersion Version => ScriptProtocolVersion.V2;

    public bool ProbeFailureMeansUnknownTicket => false;

    public Func<Task<AgentScriptStatus>> PrepareStart(ScriptDispatchSpec spec)
    {
        var command = V2ScriptDispatcher.BuildCommand(spec);

        return () => _context.Retry.ExecuteAsync(() => _raw.StartAsync(command), "StartScript", _context.Retry.StatusBudget, _context.Ct);
    }

    /// <summary>
    /// A single GetStatus. The agent answers a ticket whose record an earlier Complete released from its
    /// completion tombstone (Completed + the recorded exit code), so UnknownTicket here genuinely means the
    /// agent never saw the ticket (or the tombstone expired). No other call is made: a Cancel aimed at a
    /// ticket that turned out to be live would kill it, so it must never be used as a read.
    /// </summary>
    public Task<AgentScriptStatus> ProbeAsync(ScriptTicket ticket) => _retrying.GetStatusAsync(ticket, 0);

    public bool IsUnknownTicket(AgentScriptStatus probe) => probe == null || probe.State == AgentScriptState.UnknownTicket;

    public Task<ScriptExecutionResult> ObserveAsync(ScriptExecutionRequest request, ServiceEndPoint endpoint, ScriptTicket ticket, AgentScriptStatus initial)
        => _context.Observer.ObserveAgentScriptAsync(request.Machine, _retrying, ticket, _context.ScriptTimeout, _context.Ct, request.Masker, initial, endpoint, request.OutputSink, request.ShouldCancelRemoteScript);
}
