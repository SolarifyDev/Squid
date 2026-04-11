using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public abstract class DeploymentTransport : IDeploymentTransport
{
    public CommunicationStyle CommunicationStyle { get; }
    public IEndpointVariableContributor Variables { get; }
    public IExecutionStrategy Strategy { get; }
    public IHealthCheckStrategy HealthChecker { get; }
    public ITransportCapabilities Capabilities { get; }

    public ExecutionLocation ExecutionLocation => Capabilities.ExecutionLocation;
    public ExecutionBackend ExecutionBackend => Capabilities.ExecutionBackend;
    public bool RequiresContextPreparationForPackagedPayload => Capabilities.RequiresContextPreparationForPackagedPayload;

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
