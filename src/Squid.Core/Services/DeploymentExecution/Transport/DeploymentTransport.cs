using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public abstract class DeploymentTransport : IDeploymentTransport
{
    public CommunicationStyle CommunicationStyle { get; }
    public IEndpointVariableContributor Variables { get; }
    public IExecutionStrategy Strategy { get; }
    public IHealthCheckStrategy HealthChecker { get; }
    public ITransportCapabilities Capabilities { get; }

    protected DeploymentTransport(
        CommunicationStyle communicationStyle,
        IEndpointVariableContributor variables,
        IExecutionStrategy strategy,
        ITransportCapabilities capabilities,
        IHealthCheckStrategy healthChecker = null)
    {
        CommunicationStyle = communicationStyle;
        Variables = variables;
        Strategy = strategy;
        HealthChecker = healthChecker;
        Capabilities = capabilities ?? new TransportCapabilities();
    }
}
