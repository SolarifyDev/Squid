using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public sealed class KubernetesApiTransport(
    KubernetesApiEndpointVariableContributor variables,
    KubernetesApiScriptContextWrapper scriptWrapper,
    LocalProcessExecutionStrategy strategy)
    : DeploymentTransport(
        CommunicationStyle.KubernetesApi, variables, scriptWrapper, strategy,
        ExecutionLocation.ApiWorkerLocal, ExecutionBackend.LocalProcess,
        requiresContextPreparationForPackagedPayload: true);
