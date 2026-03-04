namespace Squid.Core.Services.DeploymentExecution.Exceptions;

public class DeploymentTargetException : InvalidOperationException
{
    public int? DeploymentId { get; }

    public DeploymentTargetException(string message, int? deploymentId = null) : base(message)
    {
        DeploymentId = deploymentId;
    }
}
