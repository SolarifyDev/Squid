using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public sealed class ServerTransport(
    ICalamariPayloadBuilder payloadBuilder,
    ILocalProcessRunner processRunner)
    : DeploymentTransport(
        CommunicationStyle.None,
        variables: null,
        scriptWrapper: null,
        new LocalProcessExecutionStrategy(payloadBuilder, processRunner),
        healthChecker: null,
        ExecutionLocation.ApiWorkerLocal,
        ExecutionBackend.LocalProcess);
