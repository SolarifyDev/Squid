using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public sealed class SshTransport : DeploymentTransport
{
    public static readonly ITransportCapabilities Capability = new TransportCapabilities
    {
        SupportedSyntaxes = TransportCapabilities.Syntaxes(ScriptSyntax.Bash, ScriptSyntax.PowerShell, ScriptSyntax.Python),
        SupportsNestedFiles = true,
        SupportsExecutableFlag = true,
        PackageStagingModes = PackageStagingMode.UploadOnly | PackageStagingMode.CacheAware,
        ExecutionLocation = ExecutionLocation.RemoteSsh,
        ExecutionBackend = ExecutionBackend.SshClient,
        SupportsOutputVariables = true,
        SupportsArtifactCollection = true,
        SupportsSudo = true,
        SupportsIsolationMutex = true,
        RequiresContextPreparationForPackagedPayload = false,
        SupportedActionTypes = TransportCapabilities.ActionTypes(
            SpecialVariables.ActionTypes.Script,
            SpecialVariables.ActionTypes.HealthCheck),
        OptionalFeatures = TransportCapabilities.Features("bash", "ssh", "sftp")
    };

    public SshTransport(
        SshEndpointVariableContributor variables,
        SshScriptContextWrapper scriptWrapper,
        SshExecutionStrategy strategy,
        SshHealthCheckStrategy healthChecker)
        : base(CommunicationStyle.Ssh, variables, scriptWrapper, strategy, Capability, healthChecker)
    {
    }
}
