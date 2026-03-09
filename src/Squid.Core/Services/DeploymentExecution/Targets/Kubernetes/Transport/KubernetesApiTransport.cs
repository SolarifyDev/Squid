using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public sealed class KubernetesApiTransport(
    KubernetesApiEndpointVariableContributor variables,
    KubernetesApiScriptContextWrapper scriptWrapper,
    LocalProcessExecutionStrategy strategy,
    KubernetesApiHealthCheckStrategy healthChecker)
    : DeploymentTransport(
        CommunicationStyle.KubernetesApi, variables, scriptWrapper, strategy, healthChecker,
        ExecutionLocation.ApiWorkerLocal, ExecutionBackend.LocalProcess,
        requiresContextPreparationForPackagedPayload: true);
