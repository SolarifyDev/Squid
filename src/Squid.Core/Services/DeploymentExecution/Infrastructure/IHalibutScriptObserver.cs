using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public interface IHalibutScriptObserver : IScopedDependency
{
    Task<ScriptExecutionResult> ObserveAndCompleteAsync(
        Machine machine,
        IAsyncScriptService scriptClient,
        ScriptTicket ticket,
        TimeSpan scriptTimeout,
        CancellationToken ct,
        SensitiveValueMasker masker,
        ScriptStatusResponse initialStartResponse = null,
        ServiceEndPoint endpoint = null,
        ScriptOutputSink outputSink = null);

    /// <summary>
    /// Same as the overload above, plus a <paramref name="shouldCancelRemoteScript"/> decision that
    /// tells the observer whether an interruption makes the script's outcome final. See
    /// <see cref="RemoteScriptCancelDecision"/>. Passing <see langword="null"/> is identical to the
    /// overload above (cancel on every interruption).
    /// </summary>
    Task<ScriptExecutionResult> ObserveAndCompleteAsync(
        Machine machine,
        IAsyncScriptService scriptClient,
        ScriptTicket ticket,
        TimeSpan scriptTimeout,
        CancellationToken ct,
        SensitiveValueMasker masker,
        ScriptStatusResponse initialStartResponse,
        ServiceEndPoint endpoint,
        ScriptOutputSink outputSink,
        RemoteScriptCancelDecision shouldCancelRemoteScript);

    /// <summary>
    /// The protocol-neutral observation both overloads above run on (they adapt a V1 client and status to
    /// it): V2's explicit states reach the loop unflattened — a lost result or unknown ticket fails the
    /// action instead of being mistaken for an exit code, and a Complete that answers "not yet complete"
    /// keeps observing.
    /// </summary>
    Task<ScriptExecutionResult> ObserveAgentScriptAsync(
        Machine machine,
        IScriptAgentClient scriptClient,
        ScriptTicket ticket,
        TimeSpan scriptTimeout,
        CancellationToken ct,
        SensitiveValueMasker masker,
        AgentScriptStatus initialStartResponse,
        ServiceEndPoint endpoint,
        ScriptOutputSink outputSink,
        RemoteScriptCancelDecision shouldCancelRemoteScript);
}
