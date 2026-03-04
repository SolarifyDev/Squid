using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution;

public interface IDeploymentTransport : IScopedDependency
{
    CommunicationStyle CommunicationStyle { get; }
    IEndpointVariableContributor Variables { get; }
    IScriptContextWrapper ScriptWrapper { get; }
    IExecutionStrategy Strategy { get; }
    ExecutionLocation ExecutionLocation { get; }
    ExecutionBackend ExecutionBackend { get; }
    bool RequiresContextPreparationForPackagedPayload { get; }
}
