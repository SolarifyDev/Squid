using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

public sealed class OpenClawTransport(
    OpenClawEndpointVariableContributor variables,
    OpenClawScriptContextWrapper scriptWrapper,
    OpenClawExecutionStrategy strategy,
    OpenClawHealthCheckStrategy healthChecker)
    : DeploymentTransport(
        CommunicationStyle.OpenClaw, variables, scriptWrapper, strategy, healthChecker,
        ExecutionLocation.ApiWorkerLocal, ExecutionBackend.HttpApi,
        requiresContextPreparationForPackagedPayload: false);
