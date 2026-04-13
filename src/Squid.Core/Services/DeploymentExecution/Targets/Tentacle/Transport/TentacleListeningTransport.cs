using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

public sealed class TentacleListeningTransport : DeploymentTransport
{
    public static readonly ITransportCapabilities Capability = new TransportCapabilities
    {
        SupportedSyntaxes = TransportCapabilities.Syntaxes(ScriptSyntax.Bash, ScriptSyntax.PowerShell),
        SupportsNestedFiles = true,
        SupportsExecutableFlag = true,
        PackageStagingModes = PackageStagingMode.UploadOnly | PackageStagingMode.CacheAware,
        ExecutionLocation = ExecutionLocation.RemoteTentacle,
        ExecutionBackend = ExecutionBackend.HalibutScriptService,
        SupportsOutputVariables = true,
        SupportsArtifactCollection = true,
        SupportsSudo = false,
        SupportsIsolationMutex = true,
        RequiresContextPreparationForPackagedPayload = false,
        SupportedActionTypes = TransportCapabilities.ActionTypes(
            SpecialVariables.ActionTypes.Script,
            SpecialVariables.ActionTypes.HealthCheck),
        OptionalFeatures = TransportCapabilities.Features("halibut", "bash", "tentacle")
    };

    public TentacleListeningTransport(
        TentacleEndpointVariableContributor variables,
        HalibutMachineExecutionStrategy strategy,
        TentacleHealthCheckStrategy healthChecker)
        : base(CommunicationStyle.LinuxListening, variables, strategy, Capability, healthChecker)
    {
    }
}
