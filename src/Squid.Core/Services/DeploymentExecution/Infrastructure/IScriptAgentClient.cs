using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

/// <summary>
/// The protocol-neutral view of an agent's script service that the observer polls: status, cancel,
/// complete. Each protocol version adapts its wire contract to this, so V2's explicit states reach the
/// observer instead of being flattened into V1's magic exit codes. Starting a script is deliberately NOT
/// here (ISP): it is protocol-specific and only the dispatch session performs it.
/// </summary>
public interface IScriptAgentClient
{
    Task<AgentScriptStatus> GetStatusAsync(ScriptTicket ticket, long lastLogSequence);

    Task<AgentScriptStatus> CancelAsync(ScriptTicket ticket, long lastLogSequence);

    Task<AgentScriptCompletion> CompleteAsync(ScriptTicket ticket, long lastLogSequence);
}

/// <summary>
/// Adapts a V1 <see cref="IAsyncScriptService"/> to <see cref="IScriptAgentClient"/> one-to-one: the three
/// V1 process states map to the three neutral ones and V1's unknown-ticket sentinel stays an ordinary
/// <c>Completed</c> status with exit code -1, exactly as the observer has always seen it.
/// </summary>
public class V1ScriptAgentClient : IScriptAgentClient
{
    private readonly IAsyncScriptService _inner;

    protected V1ScriptAgentClient(IAsyncScriptService inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Keeps the retry-aware capability of the wrapped client: the observer only binds stop-retrying (and
    /// only converts a coinciding liveness fault into an unreachable pause) for a client that retries.
    /// </summary>
    public static IScriptAgentClient Wrap(IAsyncScriptService inner)
        => inner is IStopRetryingAware ? new RetryAwareV1ScriptAgentClient(inner) : new V1ScriptAgentClient(inner);

    public async Task<AgentScriptStatus> GetStatusAsync(ScriptTicket ticket, long lastLogSequence)
        => ToNeutral(await _inner.GetStatusAsync(new ScriptStatusRequest(ticket, lastLogSequence)).ConfigureAwait(false));

    public async Task<AgentScriptStatus> CancelAsync(ScriptTicket ticket, long lastLogSequence)
        => ToNeutral(await _inner.CancelScriptAsync(new CancelScriptCommand(ticket, lastLogSequence)).ConfigureAwait(false));

    public async Task<AgentScriptCompletion> CompleteAsync(ScriptTicket ticket, long lastLogSequence)
    {
        var response = await _inner.CompleteScriptAsync(new CompleteScriptCommand(ticket, lastLogSequence)).ConfigureAwait(false);

        return new AgentScriptCompletion(AgentCompleteOutcome.Completed, ToNeutral(response));
    }

    public static AgentScriptStatus ToNeutral(ScriptStatusResponse response)
        => new(response.Ticket, ToNeutral(response.State), response.ExitCode, response.Logs, response.NextLogSequence);

    public static ScriptStatusResponse ToV1(AgentScriptStatus status)
        => new(status.Ticket, ToV1(status.State), status.ExitCode, status.Logs, status.NextLogSequence);

    private static AgentScriptState ToNeutral(ProcessState state) => state switch
    {
        ProcessState.Pending => AgentScriptState.Pending,
        ProcessState.Running => AgentScriptState.Running,
        _ => AgentScriptState.Completed
    };

    private static ProcessState ToV1(AgentScriptState state) => state switch
    {
        AgentScriptState.Pending => ProcessState.Pending,
        AgentScriptState.Running => ProcessState.Running,
        AgentScriptState.Completed => ProcessState.Complete,
        _ => throw new InvalidOperationException($"V1 cannot represent the script state {state}")
    };

    private sealed class RetryAwareV1ScriptAgentClient : V1ScriptAgentClient, IStopRetryingAware
    {
        private readonly IStopRetryingAware _aware;

        public RetryAwareV1ScriptAgentClient(IAsyncScriptService inner) : base(inner)
        {
            _aware = (IStopRetryingAware)inner;
        }

        public void BindStopRetrying(CancellationToken stopRetrying) => _aware.BindStopRetrying(stopRetrying);
    }
}
