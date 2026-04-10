using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

public sealed class OpenClawTransport : DeploymentTransport
{
    public static readonly ITransportCapabilities Capability = new TransportCapabilities
    {
        SupportedSyntaxes = TransportCapabilities.Syntaxes(ScriptSyntax.Bash),
        SupportsNestedFiles = false,
        SupportsExecutableFlag = false,
        PackageStagingModes = PackageStagingMode.None,
        ExecutionLocation = ExecutionLocation.ApiWorkerLocal,
        ExecutionBackend = ExecutionBackend.HttpApi,
        SupportsOutputVariables = true,
        SupportsArtifactCollection = false,
        SupportsSudo = false,
        SupportsIsolationMutex = false,
        RequiresContextPreparationForPackagedPayload = false,
        SupportedActionTypes = TransportCapabilities.ActionTypes(
            SpecialVariables.ActionTypes.OpenClawInvokeTool,
            SpecialVariables.ActionTypes.OpenClawRunAgent,
            SpecialVariables.ActionTypes.OpenClawWake,
            SpecialVariables.ActionTypes.OpenClawWaitSession,
            SpecialVariables.ActionTypes.OpenClawAssert,
            SpecialVariables.ActionTypes.OpenClawFetchResult,
            SpecialVariables.ActionTypes.OpenClawChatCompletion),
        OptionalFeatures = TransportCapabilities.Features("openclaw", "httpapi")
    };

    public OpenClawTransport(
        OpenClawEndpointVariableContributor variables,
        OpenClawScriptContextWrapper scriptWrapper,
        OpenClawExecutionStrategy strategy,
        OpenClawHealthCheckStrategy healthChecker)
        : base(CommunicationStyle.OpenClaw, variables, scriptWrapper, strategy, Capability, healthChecker)
    {
    }
}
