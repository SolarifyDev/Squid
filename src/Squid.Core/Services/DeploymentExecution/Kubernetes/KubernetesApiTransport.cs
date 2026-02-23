using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesApiTransport : IDeploymentTransport
{
    public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesApi;
    public IEndpointVariableContributor Variables { get; }
    public IScriptContextWrapper ScriptWrapper { get; }
    public IExecutionStrategy Strategy { get; }

    public KubernetesApiTransport(
        KubernetesApiEndpointVariableContributor variables,
        KubernetesApiScriptContextWrapper scriptWrapper,
        KubernetesApiExecutionStrategy strategy)
    {
        Variables = variables;
        ScriptWrapper = scriptWrapper;
        Strategy = strategy;
    }
}
