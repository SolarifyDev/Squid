using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public sealed class KubernetesAgentTransport(
    KubernetesAgentEndpointVariableContributor variables,
    KubernetesAgentScriptContextWrapper scriptWrapper,
    HalibutMachineExecutionStrategy strategy,
    KubernetesAgentHealthCheckStrategy healthChecker)
    : DeploymentTransport(
        CommunicationStyle.KubernetesAgent, variables, scriptWrapper, strategy, healthChecker,
        ExecutionLocation.RemoteTentacle, ExecutionBackend.HalibutScriptService,
        requiresContextPreparationForPackagedPayload: true);
