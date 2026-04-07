using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public sealed class SshTransport(
    SshEndpointVariableContributor variables,
    SshScriptContextWrapper scriptWrapper,
    SshExecutionStrategy strategy,
    SshHealthCheckStrategy healthChecker)
    : DeploymentTransport(
        CommunicationStyle.Ssh, variables, scriptWrapper, strategy, healthChecker,
        ExecutionLocation.RemoteSsh, ExecutionBackend.SshClient,
        requiresContextPreparationForPackagedPayload: false);
