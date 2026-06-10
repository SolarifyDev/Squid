namespace Squid.Core.Services.Deployments.ServerTask.Exceptions;

public class ServerTaskAwaitingInterruptionException : InvalidOperationException
{
    public int TaskId { get; }

    public ServerTaskAwaitingInterruptionException(int taskId)
        : base($"ServerTask {taskId} is paused awaiting a manual interruption response; submit the interruption to resume rather than resuming directly")
    {
        TaskId = taskId;
    }
}
