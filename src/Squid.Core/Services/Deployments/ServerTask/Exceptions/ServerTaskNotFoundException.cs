namespace Squid.Core.Services.Deployments.ServerTask.Exceptions;

public class ServerTaskNotFoundException : InvalidOperationException
{
    public int TaskId { get; }

    public ServerTaskNotFoundException(int taskId)
        : base($"ServerTask {taskId} not found")
    {
        TaskId = taskId;
    }
}
