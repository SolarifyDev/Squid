using Halibut;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers;

/// <summary>
/// The V1 conversation with an agent. Behaviour is exactly what the strategy did before versions existed:
/// StartScript on the RAW client (never retried), GetStatus / observation through the retry-decorated
/// client and the V1 observer overload; the neutral <see cref="AgentScriptStatus"/> is a lossless
/// re-spelling of the three V1 process states, so the observer sees the same statuses it always did and
/// V1's "unknown ticket" stays the -1 sentinel on a completed status (<see cref="V1ScriptDispatcher.IsUnknownTicketProbe"/>).
/// </summary>
internal sealed class V1ScriptSession : IScriptAgentSession
{
    private readonly V1ScriptDispatcher _dispatcher;
    private readonly IAsyncScriptService _raw;
    private readonly RetryingScriptClient _retrying;
    private readonly ScriptSessionContext _context;

    public V1ScriptSession(V1ScriptDispatcher dispatcher, IAsyncScriptService raw, RetryingScriptClient retrying, ScriptSessionContext context)
    {
        _dispatcher = dispatcher;
        _raw = raw;
        _retrying = retrying;
        _context = context;
    }

    public ScriptProtocolVersion Version => ScriptProtocolVersion.V1;

    public bool ProbeFailureMeansUnknownTicket => true;

    public Func<Task<AgentScriptStatus>> PrepareStart(ScriptDispatchSpec spec)
    {
        var start = _dispatcher.PrepareStart(_raw, spec);

        return async () => V1ScriptAgentClient.ToNeutral(await start().ConfigureAwait(false));
    }

    public async Task<AgentScriptStatus> ProbeAsync(ScriptTicket ticket)
        => V1ScriptAgentClient.ToNeutral(await _retrying.GetStatusAsync(new ScriptStatusRequest(ticket, 0)).ConfigureAwait(false));

    public Task CancelAsync(ScriptTicket ticket) => _retrying.CancelScriptAsync(new CancelScriptCommand(ticket, 0));

    public bool IsUnknownTicket(AgentScriptStatus probe)
        => _dispatcher.IsUnknownTicket(probe == null ? null : V1ScriptAgentClient.ToV1(probe));

    public Task<ScriptExecutionResult> ObserveAsync(ScriptExecutionRequest request, ServiceEndPoint endpoint, ScriptTicket ticket, AgentScriptStatus initial)
        => _context.Observer.ObserveAndCompleteAsync(request.Machine, _retrying, ticket, _context.ScriptTimeout, _context.Ct, request.Masker, V1ScriptAgentClient.ToV1(initial), endpoint, request.OutputSink, request.ShouldCancelRemoteScript);
}
