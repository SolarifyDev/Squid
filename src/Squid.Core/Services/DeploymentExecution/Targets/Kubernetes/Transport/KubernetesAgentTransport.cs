using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public sealed class KubernetesAgentTransport(
    KubernetesAgentEndpointVariableContributor variables,
    KubernetesAgentScriptContextWrapper scriptWrapper,
    HalibutMachineExecutionStrategy strategy)
    : DeploymentTransport(
        CommunicationStyle.KubernetesAgent, variables, scriptWrapper, strategy,
        ExecutionLocation.RemoteTentacle, ExecutionBackend.HalibutScriptService,
        requiresContextPreparationForPackagedPayload: false);
