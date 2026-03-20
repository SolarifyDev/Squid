namespace Squid.Core.Services.DeploymentExecution.Exceptions;

public class DeploymentTimeoutException(int serverTaskId, TimeSpan timeout)
    : InvalidOperationException($"Deployment timed out after {timeout.TotalMinutes:F0} minutes (task {serverTaskId})")
{
    public int ServerTaskId { get; } = serverTaskId;
    public TimeSpan Timeout { get; } = timeout;
}
