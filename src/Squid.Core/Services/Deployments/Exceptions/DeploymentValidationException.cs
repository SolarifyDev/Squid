namespace Squid.Core.Services.Deployments.Exceptions;

public class DeploymentValidationException : InvalidOperationException
{
    public DeploymentValidationException(string message) : base(message)
    {
    }
}
