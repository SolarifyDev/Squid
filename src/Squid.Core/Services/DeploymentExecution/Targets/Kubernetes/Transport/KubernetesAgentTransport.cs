using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public sealed class KubernetesAgentTransport : DeploymentTransport
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
        OptionalFeatures = TransportCapabilities.Features("kubectl", "helm", "kustomize", "halibut")
    };

    public KubernetesAgentTransport(
        KubernetesAgentEndpointVariableContributor variables,
        HalibutMachineExecutionStrategy strategy,
        KubernetesAgentHealthCheckStrategy healthChecker)
        : base(CommunicationStyle.KubernetesAgent, variables, strategy, Capability, healthChecker)
    {
    }
}
