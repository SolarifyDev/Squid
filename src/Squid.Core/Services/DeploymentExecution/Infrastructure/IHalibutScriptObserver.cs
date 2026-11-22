using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;

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
}
