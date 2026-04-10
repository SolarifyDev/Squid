using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public interface IDeploymentTransport : IScopedDependency
{
    CommunicationStyle CommunicationStyle { get; }
    IEndpointVariableContributor Variables { get; }
    IScriptContextWrapper ScriptWrapper { get; }
    IExecutionStrategy Strategy { get; }
    IHealthCheckStrategy HealthChecker { get; }

    ITransportCapabilities Capabilities => new TransportCapabilities
    {
        ExecutionLocation = ExecutionLocation,
        ExecutionBackend = ExecutionBackend,
        RequiresContextPreparationForPackagedPayload = RequiresContextPreparationForPackagedPayload
    };

    ExecutionLocation ExecutionLocation { get; }

    ExecutionBackend ExecutionBackend { get; }

    bool RequiresContextPreparationForPackagedPayload { get; }
}
