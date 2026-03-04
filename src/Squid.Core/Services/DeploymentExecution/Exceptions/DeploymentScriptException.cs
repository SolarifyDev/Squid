namespace Squid.Core.Services.DeploymentExecution.Exceptions;

public class DeploymentScriptException : InvalidOperationException
{
    public int? DeploymentId { get; }

    public DeploymentScriptException(string message, int? deploymentId = null) : base(message)
    {
        DeploymentId = deploymentId;
    }
}
