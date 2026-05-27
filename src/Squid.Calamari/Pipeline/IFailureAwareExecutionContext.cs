namespace Squid.Calamari.Pipeline;

/// <summary>
/// Opt-in marker for execution contexts whose <c>IAlwaysRunExecutionStep</c>
/// cleanup-phase steps need to know whether the main execution phase failed.
///
/// <para><b>Canonical use case</b>: the DeployFailed convention hook —
/// runs a <c>DeployFailed.sh</c> script ONLY when the operator's main
/// deploy raised (exception in any pre-cleanup step). Without this flag,
/// an <c>IAlwaysRunExecutionStep</c> can't distinguish "main script
/// succeeded, just doing cleanup" from "main script crashed, run my
/// failure-handler".</para>
///
/// <para><b>Contract</b>: <see cref="ExecutionPipeline{TContext}"/> sets
/// the flag to <c>true</c> in its catch block (the SourceException is
/// preserved for re-throw at end of pipeline as before; the flag is just
/// an additional signal for cleanup steps). Contexts that don't implement
/// this interface get the existing behaviour — cleanup steps run
/// unconditionally as before.</para>
///
/// <para><b>Why an opt-in interface vs hard-coded property on every
/// context</b>: avoids forcing the property into the existing
/// IPathBasedExecutionContext / IVariableLoadingExecutionContext etc.
/// surfaces. Same lightweight extension pattern as those interfaces.
/// Pinned by <c>ExecutionPipeline_SetsFailureFlag_OnException</c>.</para>
/// </summary>
public interface IFailureAwareExecutionContext
{
    /// <summary>
    /// <c>true</c> when at least one non-cleanup pipeline step raised an
    /// exception. Set by <see cref="ExecutionPipeline{TContext}"/>;
    /// cleanup-phase steps read this to decide whether to fire.
    /// </summary>
    bool ExecutionFailed { get; set; }
}
