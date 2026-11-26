namespace Squid.Message.Enums.Deployments;

/// <summary>
/// The outcome a run's completion step actually left the task in — which may differ from the one it set out to write (a user's cancel that landed first
/// wins, another run may own the task). Whatever the run announces (lifecycle events, audit history, task log) is derived from this, never from the intent.
/// </summary>
public enum DeploymentOutcome
{
    /// <summary>Nothing was recorded by this run (the task belongs to another run, or was already resolved): there is nothing for this run to announce.</summary>
    NotRecorded = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3,
    Paused = 4
}
