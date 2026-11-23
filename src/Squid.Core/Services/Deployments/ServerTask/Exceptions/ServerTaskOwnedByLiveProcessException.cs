namespace Squid.Core.Services.Deployments.ServerTask.Exceptions;

/// <summary>
/// Thrown when a pipeline is delivered a task that is already <see cref="TaskState.Executing"/> and whose lease a DIFFERENT run still
/// renews (a duplicate delivery: two resumes, a dashboard requeue, a duplicate fetch), or that is <see cref="TaskState.Cancelling"/> (its owner is
/// finishing the cancel, or the reaper completes it). The runner suppresses it without touching the task, so the owner keeps running alone.
/// </summary>
public sealed class ServerTaskOwnedByLiveProcessException : InvalidOperationException
{
    public int ServerTaskId { get; }

    public string LeaseOwner { get; }

    public ServerTaskOwnedByLiveProcessException(int serverTaskId, string leaseOwner)
        : base($"Task {serverTaskId} is already running under live owner '{leaseOwner}'")
    {
        ServerTaskId = serverTaskId;
        LeaseOwner = leaseOwner;
    }
}
