using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public sealed class ServerTransport : DeploymentTransport
{
    public static readonly ITransportCapabilities Capability = new TransportCapabilities
    {
        SupportedSyntaxes = TransportCapabilities.Syntaxes(ScriptSyntax.Bash, ScriptSyntax.PowerShell, ScriptSyntax.Python),
        SupportsNestedFiles = true,
        SupportsExecutableFlag = true,
        PackageStagingModes = PackageStagingMode.UploadOnly,
        ExecutionLocation = ExecutionLocation.ApiWorkerLocal,
        ExecutionBackend = ExecutionBackend.LocalProcess,
        SupportsOutputVariables = true,
        SupportsArtifactCollection = true,
        SupportsSudo = false,
        SupportsIsolationMutex = false,
        RequiresContextPreparationForPackagedPayload = false,
        SupportedActionTypes = TransportCapabilities.ActionTypes(
            SpecialVariables.ActionTypes.Script,
            SpecialVariables.ActionTypes.HealthCheck),
        OptionalFeatures = TransportCapabilities.Features("server", "local")
    };

    public ServerTransport(
        ICalamariPayloadBuilder payloadBuilder,
        ILocalProcessRunner processRunner)
        : base(
            CommunicationStyle.None,
            variables: null,
            scriptWrapper: null,
            new LocalProcessExecutionStrategy(payloadBuilder, processRunner),
            Capability,
            healthChecker: null)
    {
    }
}
