using System.Collections.Generic;
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

    // Calamari ResourceStatusReportScriptWrapper.IsEnabled (Calamari/Kubernetes/ResourceStatus/
    // ResourceStatusReportScriptWrapper.cs): "A blue/green or deployment wait is waiting for other
    // things, so we don't run resource status check" — DeploymentWait=wait is evaluated FIRST and
    // the legacy rollout wait runs whatever ResourceStatusCheck says. Squid has one status check, so
    // a legacy wait must switch it on even when ResourceStatusCheck is False.
    [Theory]
    [InlineData("Wait", null, "True", true)]
    [InlineData("Wait", "False", "True", true)]
    [InlineData("Wait", "True", "True", true)]
    [InlineData(" wait ", "False", "True", true)]
    [InlineData("NoWait", "False", "False", false)]
    [InlineData("NoWait", "True", "True", false)]
    [InlineData(null, "False", "False", false)]
    [InlineData(null, "True", "True", false)]
    public void Map_LegacyDeploymentWait_TakesPrecedenceOverResourceStatusCheckLikeCalamari(string deploymentWait, string resourceStatusCheck, string expectedStatusCheck, bool expectWaitDiagnostic)
    {
        var properties = new List<(string Key, string Value)>();

        if (deploymentWait != null) properties.Add((P + "DeploymentWait", deploymentWait));
        if (resourceStatusCheck != null) properties.Add(("Octopus.Action.Kubernetes.ResourceStatusCheck", resourceStatusCheck));

        var result = Map(AllButCustomResource, properties.ToArray());

        result.Action.Properties.Where(p => p.PropertyName == SquidP + "ObjectStatusCheck").Select(p => p.PropertyValue).ShouldBe(new[] { expectedStatusCheck });
        result.Diagnostics.Any(d => d.Code == OctopusImportActionMappingDiagnosticCodes.DeploymentWaitMappedToStatusCheck).ShouldBe(expectWaitDiagnostic);
    }

    [Fact]
    public void Map_DeploymentWaitWithDeploymentTimeout_CarriesTheTimeoutToTheStatusCheck()
    {
        var result = Map(AllButCustomResource, (P + "DeploymentWait", "Wait"), ("Octopus.Action.Kubernetes.DeploymentTimeout", "600"));

        Property(result.Action, SquidP + "ObjectStatusCheck").ShouldBe("True");
        Property(result.Action, SquidP + "ObjectStatusCheckTimeout").ShouldBe("600");
    }

    // ========== Feature gating is crash-free on the case-sensitive production dictionary ==========

    [Fact]
    public void Map_CaseVariantKeysSurvivingTheFilter_DoesNotThrowAndKeepsTheFirst()
    {
        var action = CaseSensitiveAction(AllButCustomResource.Replace(",Octopus.Features.KubernetesSecret", string.Empty), (P + "SecretName", "sec"), (P + "Namespace", "apps"), ("octopus.action.kubernetescontainers.namespace", "other"));

        var result = _mapper.Map(action, Context());

        HasProperty(result.Action, SquidP + "SecretName").ShouldBeFalse();
        Property(result.Action, SquidP + "Namespace").ShouldBe("apps");
        action.Properties.Count.ShouldBe(4);
    }

    [Fact]
    public void Map_CaseVariantKeysOwnedByADisabledFeature_AreBothDropped()
    {
        var action = CaseSensitiveAction("Octopus.Features.KubernetesService", (P + "ConfigMapName", "a"), ("octopus.action.kubernetescontainers.configmapname", "b"), (P + "ServiceName", "svc"));

        var result = _mapper.Map(action, Context());

        HasProperty(result.Action, SquidP + "ConfigMapName").ShouldBeFalse();
        Property(result.Action, SquidP + "ServiceName").ShouldBe("svc");
        result.Diagnostics.Count(d => d.Code == OctopusImportActionMappingDiagnosticCodes.DisabledFeaturePropertiesOmitted).ShouldBe(1);
    }

    [Theory]
    [InlineData(" Octopus.Features.KubernetesService ; octopus.features.kubernetesingress ")]
    [InlineData("OCTOPUS.FEATURES.KUBERNETESSERVICE,Octopus.Features.KubernetesIngress,Octopus.Features.Unknown")]
    public void Map_EnabledFeaturesSeparatorsAndCase_AreTolerated(string enabledFeatures)
    {
        var result = Map(enabledFeatures, (P + "ServiceName", "svc"), (P + "IngressName", "ing"), (P + "ConfigMapName", "cm"));

        Property(result.Action, SquidP + "ServiceName").ShouldBe("svc");
        Property(result.Action, SquidP + "IngressName").ShouldBe("ing");
        HasProperty(result.Action, SquidP + "ConfigMapName").ShouldBeFalse();
    }

    [Fact]
    public void Map_EnabledFeaturesKeyInDifferentCase_StillGates()
    {
        var action = Action(null, ("octopus.action.enabledfeatures", "Octopus.Features.KubernetesService"), (P + "ServiceName", "svc"), (P + "IngressName", "ing"));

        var result = _mapper.Map(action, Context());

        Property(result.Action, SquidP + "ServiceName").ShouldBe("svc");
        HasProperty(result.Action, SquidP + "IngressName").ShouldBeFalse();
    }

    // ========== Disabled ConfigMap / Secret still referenced by the pod ==========

    [Theory]
    [InlineData("ConfigMap", "Octopus.Features.KubernetesSecret", "ConfigMapName", "app-config", """[{"Name":"cfg","Type":"ConfigMap","ReferenceName":"","ResourceNameMode":"LinkedResource"}]""", null, "volume cfg")]
    [InlineData("Secret", "Octopus.Features.KubernetesConfigMap", "SecretName", "app-secret", """[{"Name":"secret-vol","Type":"Secret","ReferenceName":"app-secret","ResourceNameMode":"LinkedResource"}]""", null, "volume secret-vol")]
    [InlineData("ConfigMap", "Octopus.Features.KubernetesSecret", null, null, """[{"Name":"cfg","Type":"ConfigMap","ResourceNameMode":"LinkedResource"}]""", null, "volume cfg")]
    [InlineData("ConfigMap", "Octopus.Features.KubernetesSecret", "ConfigMapName", "app-config", null, """[{"Name":"web","ConfigMapEnvFromSource":[{"key":"app-config"}]}]""", "container web")]
    [InlineData("ConfigMap", "Octopus.Features.KubernetesSecret", "ConfigMapName", "app-config", null, """[{"Name":"web","ConfigMapEnvironmentVariables":[{"key":"MODE","value":"app-config","option":"mode"}]}]""", "container web")]
    [InlineData("Secret", "Octopus.Features.KubernetesConfigMap", "SecretName", "app-secret", null, """[{"Name":"web","SecretEnvFromSource":[{"key":"app-secret"}]}]""", "container web")]
    [InlineData("Secret", "Octopus.Features.KubernetesConfigMap", "SecretName", "app-secret", null, """[{"Name":"web","SecretEnvironmentVariables":[{"key":"PWD","value":"app-secret","option":"pwd"}]}]""", "container web")]
    public void Map_DisabledConfigMapOrSecretStillReferencedByThePod_Warns(string displayName, string enabledFeatures, string nameKey, string name, string volumes, string containers, string expectedReferrer)
    {
        var properties = new List<(string Key, string Value)>();

        if (nameKey != null) properties.Add((P + nameKey, name));
        if (volumes != null) properties.Add((P + "CombinedVolumes", volumes));
        if (containers != null) properties.Add((P + "Containers", containers));

        var result = Map(enabledFeatures, properties.ToArray());

        var warning = result.Diagnostics.Single(d => d.Code == OctopusImportActionMappingDiagnosticCodes.DisabledFeatureResourceReferenced);
        warning.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        warning.Message.ShouldContain(displayName);
        warning.Message.ShouldContain(expectedReferrer);
    }

    [Theory]
    [InlineData("Octopus.Features.KubernetesConfigMap,Octopus.Features.KubernetesSecret", """[{"Name":"cfg","Type":"ConfigMap","ReferenceName":"","ResourceNameMode":"LinkedResource"}]""", """[{"Name":"web","ConfigMapEnvFromSource":[{"key":"app-config"}]}]""")]
    [InlineData("", """[{"Name":"ext","Type":"ConfigMap","ReferenceName":"external-cm","ResourceNameMode":"CustomResource"}]""", """[{"Name":"web","ConfigMapEnvFromSource":[{"key":"external-cm"}]}]""")]
    [InlineData(null, """[{"Name":"cfg","Type":"ConfigMap","ReferenceName":"","ResourceNameMode":"LinkedResource"}]""", null)]
    [InlineData("", "not json", "{also not json")]
    // A reference is the step's own resource only when it equals the name ordinally — the identity rule the
    // YAML generator uses (KubernetesContainersActionYamlGenerator.IsStepResourceReference); anything else is
    // an externally managed resource, so a case or whitespace variant must not be reported as dangling.
    [InlineData("", "[]", """[{"Name":"web","ConfigMapEnvFromSource":[{"key":"App-Config"}]}]""")]
    [InlineData("", "[]", """[{"Name":"web","ConfigMapEnvFromSource":[{"key":" app-config "}]}]""")]
    [InlineData("", "[]", """[{"Name":"web","ConfigMapEnvironmentVariables":[{"key":"MODE","value":"APP-CONFIG","option":"mode"}]}]""")]
    public void Map_NoDanglingReferenceToADisabledResource_DoesNotWarn(string enabledFeatures, string volumes, string containers)
    {
        var properties = new List<(string Key, string Value)> { (P + "ConfigMapName", "app-config"), (P + "CombinedVolumes", volumes) };

        if (containers != null) properties.Add((P + "Containers", containers));

        var result = Map(enabledFeatures, properties.ToArray());

        result.Diagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportActionMappingDiagnosticCodes.DisabledFeatureResourceReferenced);
    }

    // ========== Non-Deployment workload types ==========

    [Theory]
    [InlineData("StatefulSet")]
    [InlineData("DaemonSet")]
    [InlineData("Job")]
    [InlineData(" statefulset ")]
    public void Map_NonDeploymentWorkload_KeepsTypeGatesFeaturesAndMapsWaitAndCustomResource(string resourceType)
    {
        var result = Map("Octopus.Features.KubernetesCustomResource", (P + "DeploymentResourceType", resourceType), (P + "ServiceName", "svc"), (P + "DeploymentWait", "Wait"), (P + "CustomResourceYaml", CustomResourceYaml));

        Property(result.Action, SquidP + "DeploymentResourceType").ShouldBe(resourceType);
        HasProperty(result.Action, SquidP + "ServiceName").ShouldBeFalse();
        Property(result.Action, SquidP + "ObjectStatusCheck").ShouldBe("True");
        Property(result.Action, SpecialVariables.Action.KubernetesYaml).ShouldBe(CustomResourceYaml);
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

    // Production DTOs come from System.Text.Json with the default (case-sensitive) comparer, so a
    // malformed export can carry keys that differ only by case.
    private static OctopusDeploymentActionDto CaseSensitiveAction(string enabledFeatures, params (string Key, string Value)[] properties)
    {
        var action = new OctopusDeploymentActionDto { Id = "Actions-1", Name = "Deploy web", ActionType = "Octopus.KubernetesDeployContainers", Properties = new(StringComparer.Ordinal) };

        action.Properties["Octopus.Action.EnabledFeatures"] = enabledFeatures;

        foreach (var (key, value) in properties) action.Properties[key] = value;

        return action;
    }

    private static OctopusImportActionMappingContext Context() => new(new OctopusImportIdMap(), 42);

    private static bool HasProperty(CreateOrUpdateDeploymentActionModel action, string name) => action.Properties.Any(p => p.PropertyName == name);

    private static string Property(CreateOrUpdateDeploymentActionModel action, string name) => action.Properties.Single(p => p.PropertyName == name).PropertyValue;
}
