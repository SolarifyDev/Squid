using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public interface IDeploymentTransport : IScopedDependency
{
    CommunicationStyle CommunicationStyle { get; }
    IEndpointVariableContributor Variables { get; }
    IExecutionStrategy Strategy { get; }
    IHealthCheckStrategy HealthChecker { get; }
    ITransportCapabilities Capabilities { get; }
}
