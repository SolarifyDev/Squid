using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution;

public abstract class DeploymentTransport : IDeploymentTransport
{
    public CommunicationStyle CommunicationStyle { get; }
    public IEndpointVariableContributor Variables { get; }
    public IScriptContextWrapper ScriptWrapper { get; }
    public IExecutionStrategy Strategy { get; }

    protected DeploymentTransport(
        CommunicationStyle communicationStyle,
        IEndpointVariableContributor variables,
        IScriptContextWrapper scriptWrapper,
        IExecutionStrategy strategy)
    {
        CommunicationStyle = communicationStyle;
        Variables = variables;
        ScriptWrapper = scriptWrapper;
        Strategy = strategy;
    }
}
