using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.OctopusImport.Mapping.Actions;

public class OctopusKubernetesContainersFeatureMappingTests
{
    private const string P = "Octopus.Action.KubernetesContainers.";
    private const string SquidP = "Squid.Action.KubernetesContainers.";
    private const string AllButCustomResource = "Octopus.Features.KubernetesService,Octopus.Features.KubernetesIngress,Octopus.Features.KubernetesConfigMap,Octopus.Features.KubernetesSecret";
    private const string CustomResourceYaml = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: extra\n";

    private readonly OctopusKubernetesDeployContainersActionMapper _mapper = new();

    [Fact]
    public void Map_CustomResourceFeatureDisabled_DropsTheYamlLikeOctopusDidWithInfo()
    {
        var result = Map(AllButCustomResource, (P + "CustomResourceYaml", CustomResourceYaml));

        HasProperty(result.Action, SpecialVariables.Action.KubernetesYaml).ShouldBeFalse();
        result.Diagnostics.Single(d => d.Code == OctopusImportActionMappingDiagnosticCodes.DisabledFeaturePropertiesOmitted).Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info);
        result.Diagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportActionMappingDiagnosticCodes.UnsupportedProperty);
    }

    [Fact]
    public void Map_CustomResourceFeatureEnabled_MapsTheYaml()
    {
        var result = Map(AllButCustomResource + ",Octopus.Features.KubernetesCustomResource", (P + "CustomResourceYaml", CustomResourceYaml));

        Property(result.Action, SpecialVariables.Action.KubernetesYaml).ShouldBe(CustomResourceYaml);
        result.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportActionMappingDiagnosticCodes.DisabledFeaturePropertiesOmitted);
    }

    [Fact]
    public void Map_EnabledFeaturesAbsent_GatesNothing()
    {
        var result = Map(null, (P + "CustomResourceYaml", CustomResourceYaml), (P + "ServiceName", "svc"));

        Property(result.Action, SpecialVariables.Action.KubernetesYaml).ShouldBe(CustomResourceYaml);
        Property(result.Action, SquidP + "ServiceName").ShouldBe("svc");
    }

    [Fact]
    public void Map_EnabledFeaturesPresentButEmpty_DropsEveryOptionalResourceButKeepsCoreAndServiceAccount()
    {
        var result = Map(string.Empty,
            (P + "ServiceName", "svc"),
            (P + "ServicePorts", "[{\"name\":\"http\",\"port\":\"80\"}]"),
            (P + "IngressName", "ing"),
            (P + "ConfigMapName", "cm"),
            (P + "SecretName", "sec"),
            (P + "ServiceAccountName", "runner"),
            (P + "DeploymentName", "web"));

        HasProperty(result.Action, SquidP + "ServiceName").ShouldBeFalse();
        HasProperty(result.Action, SquidP + "ServicePorts").ShouldBeFalse();
        HasProperty(result.Action, SquidP + "IngressName").ShouldBeFalse();
        HasProperty(result.Action, SquidP + "ConfigMapName").ShouldBeFalse();
        HasProperty(result.Action, SquidP + "SecretName").ShouldBeFalse();
        Property(result.Action, SquidP + "ServiceAccountName").ShouldBe("runner");
        Property(result.Action, SquidP + "DeploymentName").ShouldBe("web");
        result.Diagnostics.Count(d => d.Code == OctopusImportActionMappingDiagnosticCodes.DisabledFeaturePropertiesOmitted).ShouldBe(4);
    }

    [Fact]
    public void Map_DoesNotMutateTheGraphSourceAction()
    {
        var action = Action(AllButCustomResource, (P + "CustomResourceYaml", CustomResourceYaml));

        _mapper.Map(action, Context());

        action.Properties.ShouldContainKey(P + "CustomResourceYaml");
    }

    [Fact]
    public void Map_DeploymentWaitWait_EnablesSquidStatusCheck()
    {
        var result = Map(AllButCustomResource, (P + "DeploymentWait", "Wait"));

        Property(result.Action, SquidP + "ObjectStatusCheck").ShouldBe("True");
        result.Diagnostics.Single(d => d.Code == OctopusImportActionMappingDiagnosticCodes.DeploymentWaitMappedToStatusCheck).Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info);
    }

    [Fact]
    public void Map_DeploymentWaitNoWait_DoesNotEnableStatusCheck()
    {
        var result = Map(AllButCustomResource, (P + "DeploymentWait", "NoWait"));

        HasProperty(result.Action, SquidP + "ObjectStatusCheck").ShouldBeFalse();
        result.Diagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportActionMappingDiagnosticCodes.UnsupportedProperty);
    }

    [Fact]
    public void Map_ResourceStatusCheckPresent_WinsOverLegacyDeploymentWait()
    {
        var result = Map(AllButCustomResource, (P + "DeploymentWait", "Wait"), ("Octopus.Action.Kubernetes.ResourceStatusCheck", "False"));

        Property(result.Action, SquidP + "ObjectStatusCheck").ShouldBe("False");
        result.Diagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportActionMappingDiagnosticCodes.DeploymentWaitMappedToStatusCheck);
    }

    [Theory]
    [InlineData("Deployment", false)]
    [InlineData("StatefulSet", true)]
    public void Map_PodManagementPolicy_IsOnlyReportedForStatefulSets(string resourceType, bool reported)
    {
        var result = Map(AllButCustomResource, (P + "DeploymentResourceType", resourceType), (P + "PodManagementPolicy", "OrderedReady"));

        result.Diagnostics.Any(d => d.Code == OctopusImportActionMappingDiagnosticCodes.UnsupportedProperty && d.Message.Contains("PodManagementPolicy")).ShouldBe(reported);
    }

    [Fact]
    public void Map_EnabledCustomResourceIteratingOctopusParameters_Warns()
    {
        var yaml = "data:\n#{each var in OctopusParameters}\n  #{var.Key}: x\n#{/each}";

        var result = Map(AllButCustomResource + ",Octopus.Features.KubernetesCustomResource", (P + "CustomResourceYaml", yaml));

        result.Diagnostics.Single(d => d.Code == OctopusImportActionMappingDiagnosticCodes.CustomResourceUsesOctopusParameters).Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
    }

    private OctopusImportActionMappingResult Map(string enabledFeatures, params (string Key, string Value)[] properties)
        => _mapper.Map(Action(enabledFeatures, properties), Context());

    private static OctopusDeploymentActionDto Action(string enabledFeatures, params (string Key, string Value)[] properties)
    {
        var action = new OctopusDeploymentActionDto { Id = "Actions-1", Name = "Deploy web", ActionType = "Octopus.KubernetesDeployContainers", Properties = new(StringComparer.OrdinalIgnoreCase) };

        if (enabledFeatures != null) action.Properties["Octopus.Action.EnabledFeatures"] = enabledFeatures;

        foreach (var (key, value) in properties) action.Properties[key] = value;

        return action;
    }

    private static OctopusImportActionMappingContext Context() => new(new OctopusImportIdMap(), 42);

    private static bool HasProperty(CreateOrUpdateDeploymentActionModel action, string name) => action.Properties.Any(p => p.PropertyName == name);

    private static string Property(CreateOrUpdateDeploymentActionModel action, string name) => action.Properties.Single(p => p.PropertyName == name).PropertyValue;
}
