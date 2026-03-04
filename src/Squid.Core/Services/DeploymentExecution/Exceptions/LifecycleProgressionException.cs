namespace Squid.Core.Services.DeploymentExecution.Exceptions;

public class LifecycleProgressionException : Exception
{
    public int EnvironmentId { get; }

    public int LifecycleId { get; }

    public LifecycleProgressionException(int environmentId, int lifecycleId)
        : base($"Environment {environmentId} is not allowed by lifecycle {lifecycleId} progression rules")
    {
        EnvironmentId = environmentId;
        LifecycleId = lifecycleId;
    }
}
