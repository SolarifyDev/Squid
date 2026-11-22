namespace Squid.Core.Services.DeploymentExecution.Script;

/// <summary>
/// Optional decision threaded from the party that knows WHY a deployment is being interrupted down
/// to the script observer: "if the wait is interrupted right now, is the in-flight script's outcome
/// final (the task is heading to a terminal state), so the agent must be told to cancel it?"
///
/// <para>Evaluated lazily, only at the moment the observer's cancellation token fires. It must be a
/// pure read of already-set state (e.g. <c>CancellationTokenSource.IsCancellationRequested</c>), never
/// a token that is itself linked from the observer's token — a linked token can lag behind the one the
/// observer is awaiting and would race.</para>
///
/// <para>When no decision is supplied the observer keeps its historical behaviour: it cancels the
/// remote script on every interruption. A decision that returns <see langword="false"/> means the
/// task is pausing (resumable timeout, host shutdown, transient infra loss) and the script must be
/// left running so a resume can re-attach to it.</para>
/// </summary>
public delegate bool RemoteScriptCancelDecision();
