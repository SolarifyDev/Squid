using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public sealed class KubernetesApiTransport(
    KubernetesApiEndpointVariableContributor variables,
    KubernetesApiScriptContextWrapper scriptWrapper,
    KubernetesApiExecutionStrategy strategy)
    : DeploymentTransport(CommunicationStyle.KubernetesApi, variables, scriptWrapper, strategy);
