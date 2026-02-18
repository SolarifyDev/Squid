namespace Squid.Core.Services.Deployments.Exceptions;

public class DeploymentEndpointException : InvalidOperationException
{
    public string MachineName { get; }

    public DeploymentEndpointException(string machineName) : base($"Endpoint could not be parsed for machine {machineName}")
    {
        MachineName = machineName;
    }
}
