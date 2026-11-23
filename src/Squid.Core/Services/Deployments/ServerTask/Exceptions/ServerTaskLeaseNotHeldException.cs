namespace Squid.Core.Services.Deployments.ServerTask.Exceptions;

/// <summary>
/// Thrown when a pipeline run writes a running task's state (pause, success, failure, cancel) but the row is in the state it expected and its lease
/// belongs to a DIFFERENT run: the task was recovered or resumed from under this run. The write is refused so one run can never overwrite another run's
/// task; the run that gets this has lost the task and must record nothing.
/// </summary>
public sealed class ServerTaskLeaseNotHeldException : InvalidOperationException
{
    public int ServerTaskId { get; }

    public string ExpectedOwner { get; }

    public string CurrentOwner { get; }

    public ServerTaskLeaseNotHeldException(int serverTaskId, string expectedOwner, string currentOwner)
        : base($"Task {serverTaskId} is not leased to run '{expectedOwner}' (current lease owner: '{currentOwner ?? "none"}')")
    {
        ServerTaskId = serverTaskId;
        ExpectedOwner = expectedOwner;
        CurrentOwner = currentOwner;
    }
}
