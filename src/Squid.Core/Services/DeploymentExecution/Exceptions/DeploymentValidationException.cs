namespace Squid.Core.Services.DeploymentExecution.Exceptions;

public class DeploymentValidationException : InvalidOperationException
{
    public DeploymentValidationException(string message) : base(message)
    {
    }
}
