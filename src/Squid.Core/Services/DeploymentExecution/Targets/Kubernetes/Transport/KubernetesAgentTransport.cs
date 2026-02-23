using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public sealed class KubernetesAgentTransport(
    KubernetesAgentEndpointVariableContributor variables,
    KubernetesAgentScriptContextWrapper scriptWrapper,
    KubernetesAgentExecutionStrategy strategy)
    : DeploymentTransport(CommunicationStyle.KubernetesAgent, variables, scriptWrapper, strategy);
