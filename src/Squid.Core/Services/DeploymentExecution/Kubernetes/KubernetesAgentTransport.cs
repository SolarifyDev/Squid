using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesAgentTransport : IDeploymentTransport
{
    public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesAgent;
    public IEndpointVariableContributor Variables { get; }
    public IScriptContextWrapper ScriptWrapper { get; }
    public IExecutionStrategy Strategy { get; }

    public KubernetesAgentTransport(
        KubernetesAgentEndpointVariableContributor variables,
        KubernetesAgentScriptContextWrapper scriptWrapper,
        KubernetesAgentExecutionStrategy strategy)
    {
        Variables = variables;
        ScriptWrapper = scriptWrapper;
        Strategy = strategy;
    }
}
