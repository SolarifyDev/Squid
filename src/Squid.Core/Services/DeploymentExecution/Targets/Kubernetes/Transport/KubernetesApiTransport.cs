using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public sealed class KubernetesApiTransport : DeploymentTransport
{
    public static readonly ITransportCapabilities Capability = new TransportCapabilities
    {
        SupportedSyntaxes = TransportCapabilities.Syntaxes(ScriptSyntax.Bash, ScriptSyntax.PowerShell),
        SupportsNestedFiles = true,
        SupportsExecutableFlag = false,
        PackageStagingModes = PackageStagingMode.UploadOnly,
        ExecutionLocation = ExecutionLocation.ApiWorkerLocal,
        ExecutionBackend = ExecutionBackend.LocalProcess,
        SupportsOutputVariables = true,
        SupportsArtifactCollection = true,
        SupportsSudo = false,
        SupportsIsolationMutex = false,
        RequiresContextPreparationForPackagedPayload = true,
        SupportedActionTypes = TransportCapabilities.ActionTypes(
            SpecialVariables.ActionTypes.Script,
            SpecialVariables.ActionTypes.KubernetesDeployRawYaml,
            SpecialVariables.ActionTypes.KubernetesDeployContainers,
            SpecialVariables.ActionTypes.KubernetesDeployIngress,
            SpecialVariables.ActionTypes.KubernetesDeployService,
            SpecialVariables.ActionTypes.KubernetesDeployConfigMap,
            SpecialVariables.ActionTypes.KubernetesDeploySecret,
            SpecialVariables.ActionTypes.KubernetesKustomize,
            SpecialVariables.ActionTypes.HelmChartUpgrade,
            SpecialVariables.ActionTypes.HealthCheck),
        OptionalFeatures = TransportCapabilities.Features("kubectl", "helm", "kustomize")
    };

    public KubernetesApiTransport(
        KubernetesApiEndpointVariableContributor variables,
        KubernetesApiScriptContextWrapper scriptWrapper,
        LocalProcessExecutionStrategy strategy,
        KubernetesApiHealthCheckStrategy healthChecker)
        : base(CommunicationStyle.KubernetesApi, variables, scriptWrapper, strategy, Capability, healthChecker)
    {
    }
}
