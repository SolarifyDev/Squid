namespace Squid.Core.Services.DeploymentExecution.Exceptions;

public class DeploymentSuspendedException(int serverTaskId) : Exception($"Task {serverTaskId} suspended for interruption")
{
    public int ServerTaskId { get; } = serverTaskId;
}
