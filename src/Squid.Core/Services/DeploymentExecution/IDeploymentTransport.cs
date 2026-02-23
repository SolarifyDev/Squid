using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution;

public interface IDeploymentTransport : IScopedDependency
{
    CommunicationStyle CommunicationStyle { get; }
    IEndpointVariableContributor Variables { get; }
    IScriptContextWrapper ScriptWrapper { get; }
    IExecutionStrategy Strategy { get; }
}
