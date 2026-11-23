namespace Squid.Core.Services.DeploymentExecution;

/// <summary>
/// Stops the agent scripts a task still has in flight. A pause deliberately leaves the agent script running (its ticket stays on the checkpoint so
/// a resume re-attaches), so a task that is then CANCELLED without ever resuming would orphan that script unless something cancels it. Called
/// by every path that finishes a task without a running pipeline — cancelling a Paused task, the lease reaper completing a Cancelling task whose
/// owner died, the operator's force-cancel — immediately before the checkpoint (the only record of the tickets) is deleted.
///
/// <para>Best effort and bounded: an unreachable agent, an unknown machine or an unrecognised protocol is logged and skipped, never thrown, so a
/// cancel is never held hostage by an agent that is down.</para>
/// </summary>
public interface IInFlightScriptCanceller : IScopedDependency
{
    Task CancelAllAsync(int serverTaskId, CancellationToken ct = default);
}
