using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public sealed class OctopusKubernetesDeployIngressActionMapper : IOctopusImportActionMapper
{
    private static readonly Dictionary<string, string> PropertyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Octopus.Action.KubernetesContainers.IngressName"] = KubernetesProperties.IngressName,
        ["Octopus.Action.KubernetesContainers.IngressClassName"] = KubernetesProperties.IngressClassName,
        ["Octopus.Action.KubernetesContainers.Namespace"] = KubernetesProperties.Namespace
    };

    private static readonly IReadOnlySet<string> SupportedProperties = new HashSet<string>(
        PropertyMap.Keys
            .Concat([
                "Octopus.Action.KubernetesContainers.IngressAnnotations",
                "Octopus.Action.KubernetesContainers.IngressRules",
                "Octopus.Action.KubernetesContainers.IngressTlsCertificates",
                OctopusImportKubernetesActionMapperSupport.OctopusResourceStatusCheck,
                OctopusImportKubernetesActionMapperSupport.OctopusDeploymentTimeout,
                "Octopus.Action.Kubernetes.ServerSideApply.Enabled",
                "Octopus.Action.Kubernetes.ServerSideApply.FieldManager",
                "Octopus.Action.Kubernetes.ServerSideApply.ForceConflicts"
            ]),
        StringComparer.OrdinalIgnoreCase);

    public string OctopusActionType => "Octopus.KubernetesDeployIngress";

    public string SquidActionType => SpecialVariables.ActionTypes.KubernetesDeployIngress;

    public OctopusImportActionMappingResult Map(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var model = OctopusImportKubernetesActionMapperSupport.CreateActionModel(action, SquidActionType);

        OctopusImportKubernetesActionMapperSupport.AddSimpleMappedProperties(action, model.Properties, PropertyMap);
        OctopusImportKubernetesActionMapperSupport.AddKubernetesExecutionProperties(action, model.Properties);
        OctopusImportKubernetesActionMapperSupport.AddIngressAnnotations(action, model.Properties, diagnostics);
        OctopusImportKubernetesActionMapperSupport.AddIngressRules(action, model.Properties, diagnostics);
        OctopusImportKubernetesActionMapperSupport.AddIngressTlsCertificates(action, model.Properties, diagnostics);
        OctopusImportKubernetesActionMapperSupport.AddUnsupportedPropertyDiagnostics(action, SupportedProperties, diagnostics);

        return new OctopusImportActionMappingResult(model, diagnostics);
    }
}
