using System.Linq;
using System.Text.Json;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.OctopusImport.Mapping.Actions;

public class OctopusExtendedActionMapperTests
{
    [Fact]
    public void RawYamlMapper_MapsOfficialInlineYamlActionTemplateShape()
    {
        var mapper = new OctopusKubernetesDeployRawYamlActionMapper();
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-raw-yaml",
            Name = "Apply manifests",
            ActionType = "Octopus.KubernetesDeployRawYaml",
            Properties =
            {
                ["Octopus.Action.Script.ScriptSource"] = "Inline",
                ["Octopus.Action.KubernetesContainers.CustomResourceYaml"] = """
                apiVersion: autoscaling/v2
                kind: HorizontalPodAutoscaler
                metadata:
                  name: #{HorizontalPodAutoscalerName}
                """
            }
        };

        var result = mapper.Map(action, Context());

        result.HasBlockers.ShouldBeFalse();
        result.Action.ActionType.ShouldBe(SpecialVariables.ActionTypes.KubernetesDeployRawYaml);
        Property(result.Action, KubernetesRawYamlProperties.InlineYaml).ShouldContain("kind: HorizontalPodAutoscaler");
    }

    [Fact]
    public void RawYamlMapper_MapsOfficialPackageAndExecutionProperties()
    {
        var mapper = new OctopusKubernetesDeployRawYamlActionMapper();
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-raw-yaml",
            Name = "Apply manifests",
            ActionType = "Octopus.KubernetesDeployRawYaml",
            Packages =
            [
                new OctopusActionPackageDto
                {
                    PackageId = "Acme.Manifests",
                    FeedId = "Feeds-1-PREFIX-VALUES",
                    Version = "2.0.0"
                }
            ],
            Properties =
            {
                ["Octopus.Action.Kubernetes.Namespace"] = "production",
                ["Octopus.Action.Kubernetes.ResourceStatusCheck"] = "True",
                ["Octopus.Action.Kubernetes.DeploymentTimeout"] = "120",
                ["Octopus.Action.Kubernetes.ServerSideApply.Enabled"] = "True",
                ["Octopus.Action.Kubernetes.ServerSideApply.FieldManager"] = "squid-import",
                ["Octopus.Action.Kubernetes.ServerSideApply.ForceConflicts"] = "True"
            }
        };

        var result = mapper.Map(action, ContextWithFeed());

        result.HasBlockers.ShouldBeFalse();
        Property(result.Action, KubernetesProperties.Namespace).ShouldBe("production");
        Property(result.Action, SpecialVariables.Action.PackageId).ShouldBe("Acme.Manifests");
        Property(result.Action, SpecialVariables.Action.PackageFeedId).ShouldBe("301");
        Property(result.Action, SpecialVariables.Action.PackageVersion).ShouldBe("2.0.0");
        Property(result.Action, KubernetesProperties.ObjectStatusCheck).ShouldBe("True");
        Property(result.Action, KubernetesProperties.ObjectStatusCheckTimeout).ShouldBe("120");
        Property(result.Action, KubernetesProperties.ServerSideApplyEnabled).ShouldBe("True");
        Property(result.Action, KubernetesProperties.ServerSideApplyFieldManager).ShouldBe("squid-import");
        Property(result.Action, KubernetesProperties.ServerSideApplyForceConflicts).ShouldBe("True");
    }

    [Fact]
    public void RawYamlMapper_WhenSourceIsGitRepository_AddsBlocker()
    {
        var mapper = new OctopusKubernetesDeployRawYamlActionMapper();
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-raw-yaml",
            Name = "Apply Git manifests",
            ActionType = "Octopus.KubernetesDeployRawYaml",
            Properties =
            {
                ["Octopus.Action.Script.ScriptSource"] = "GitRepository",
                ["Octopus.Action.KubernetesContainers.CustomResourceYamlFileName"] = "*.yaml"
            }
        };

        var result = mapper.Map(action, Context());

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportActionMappingDiagnosticCodes.UnsupportedActionSource);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportActionMappingDiagnosticCodes.KubernetesYamlFileSelectionUnsupported);
    }

    [Fact]
    public void RawYamlMapper_DefaultManifestSelection_DoesNotAddFileSelectionBlocker()
    {
        var mapper = new OctopusKubernetesDeployRawYamlActionMapper();
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-raw-yaml",
            Name = "Apply default manifests",
            ActionType = "Octopus.KubernetesDeployRawYaml",
            Properties =
            {
                ["Octopus.Action.KubernetesContainers.CustomResourceYamlFileName"] = "./customresource.yaml"
            }
        };

        var result = mapper.Map(action, Context());

        result.HasBlockers.ShouldBeFalse();
        result.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportActionMappingDiagnosticCodes.KubernetesYamlFileSelectionUnsupported);
    }

    [Fact]
    public void HelmMapper_MapsOfficialActionTemplatePropertiesAndPackageReference()
    {
        var mapper = new OctopusHelmChartUpgradeActionMapper();
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-helm",
            Name = "Deploy chart",
            ActionType = "Octopus.HelmChartUpgrade",
            Packages =
            [
                new OctopusActionPackageDto
                {
                    PackageId = "Acme.Chart",
                    FeedId = "Feeds-1-PREFIX-VALUES",
                    Version = "3.1.4"
                }
            ],
            Properties =
            {
                ["Octopus.Action.Helm.ReleaseName"] = "web",
                ["Octopus.Action.Helm.Namespace"] = "production",
                ["Octopus.Action.Helm.ChartDirectory"] = "charts/web",
                ["Octopus.Action.Helm.CustomHelmExecutable"] = "/usr/local/bin/helm",
                ["Octopus.Action.Helm.ResetValues"] = "False",
                ["Octopus.Action.Helm.AdditionalArgs"] = "--atomic",
                ["Octopus.Action.Helm.YamlValues"] = "replicaCount: 3",
                ["Octopus.Action.Helm.KeyValues"] = """{"image.tag":"v3"}""",
                ["Octopus.Action.Helm.Timeout"] = "5m",
                ["Octopus.Action.Helm.ClientVersion"] = "V3"
            }
        };

        var result = mapper.Map(action, ContextWithFeed());

        result.HasBlockers.ShouldBeFalse();
        Property(result.Action, KubernetesHelmProperties.ReleaseName).ShouldBe("web");
        Property(result.Action, KubernetesProperties.Namespace).ShouldBe("production");
        Property(result.Action, KubernetesHelmProperties.ChartPath).ShouldBe("charts/web");
        Property(result.Action, KubernetesHelmProperties.CustomHelmExecutable).ShouldBe("/usr/local/bin/helm");
        Property(result.Action, KubernetesHelmProperties.ResetValues).ShouldBe("False");
        Property(result.Action, KubernetesHelmProperties.AdditionalArgs).ShouldBe("--atomic");
        Property(result.Action, KubernetesHelmProperties.YamlValues).ShouldBe("replicaCount: 3");
        Property(result.Action, KubernetesHelmProperties.KeyValues).ShouldContain("\"image.tag\":\"v3\"");
        Property(result.Action, KubernetesHelmProperties.Timeout).ShouldBe("5m");
        Property(result.Action, KubernetesHelmProperties.ClientVersion).ShouldBe("V3");
        Property(result.Action, SpecialVariables.Action.PackageId).ShouldBe("Acme.Chart");
        Property(result.Action, SpecialVariables.Action.PackageFeedId).ShouldBe("301");
        Property(result.Action, SpecialVariables.Action.PackageVersion).ShouldBe("3.1.4");
    }

    [Fact]
    public void HelmMapper_NormalizesInlineYamlAndKeyValuesTemplateSources()
    {
        var mapper = new OctopusHelmChartUpgradeActionMapper();
        var sources = """
        [
          { "Type": "InlineYaml", "Value": "replicaCount: 5" },
          { "Type": "KeyValues", "Value": { "image.tag": "v5", "replicaCount": 5, "enabled": false } }
        ]
        """;
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-helm",
            Name = "Deploy chart",
            ActionType = "Octopus.HelmChartUpgrade",
            Properties =
            {
                ["Octopus.Action.Helm.TemplateValuesSources"] = sources
            }
        };

        var result = mapper.Map(action, Context());

        result.HasBlockers.ShouldBeFalse();
        var normalized = Property(result.Action, KubernetesHelmProperties.ValueSources);
        using var document = JsonDocument.Parse(normalized);
        var entries = document.RootElement.EnumerateArray().ToList();

        entries[0].GetProperty("Type").GetString().ShouldBe("InlineYaml");
        entries[0].GetProperty("Value").GetString().ShouldBe("replicaCount: 5");
        entries[1].GetProperty("Type").GetString().ShouldBe("KeyValues");

        using var keyValues = JsonDocument.Parse(entries[1].GetProperty("Value").GetString());
        keyValues.RootElement.GetProperty("image.tag").GetString().ShouldBe("v5");
        keyValues.RootElement.GetProperty("replicaCount").GetString().ShouldBe("5");
        keyValues.RootElement.GetProperty("enabled").GetString().ShouldBe("False");
    }

    [Fact]
    public void HelmMapper_WhenTemplateValueSourceIsPackage_AddsBlocker()
    {
        var mapper = new OctopusHelmChartUpgradeActionMapper();
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-helm",
            Name = "Deploy chart",
            ActionType = "Octopus.HelmChartUpgrade",
            Properties =
            {
                ["Octopus.Action.Helm.TemplateValuesSources"] = """[{"Type":"Package","PackageName":"values"}]"""
            }
        };

        var result = mapper.Map(action, Context());

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.ShouldContain(d => d.Code == OctopusImportActionMappingDiagnosticCodes.UnsupportedHelmValueSource);
    }

    [Fact]
    public void HelmMapper_MalformedTemplateValuesJson_AddsBlocker()
    {
        var mapper = new OctopusHelmChartUpgradeActionMapper();
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-helm",
            Name = "Deploy chart",
            ActionType = "Octopus.HelmChartUpgrade",
            Properties =
            {
                ["Octopus.Action.Helm.TemplateValuesSources"] = "not-json"
            }
        };

        var result = mapper.Map(action, Context());

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.ShouldContain(d => d.Code == OctopusImportActionMappingDiagnosticCodes.MalformedEmbeddedJson);
    }

    [Fact]
    public void TentaclePackageMapper_MapsOfficialPackageReferenceAndVersionedDirectory()
    {
        var mapper = new OctopusTentaclePackageActionMapper();
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-package",
            Name = "Deploy package",
            ActionType = "Octopus.TentaclePackage",
            Packages =
            [
                new OctopusActionPackageDto
                {
                    PackageId = "Acme.Web",
                    FeedId = "Feeds-1-PREFIX-VALUES",
                    Version = "1.2.3"
                }
            ]
        };

        var result = mapper.Map(action, ContextWithFeed());

        result.HasBlockers.ShouldBeFalse();
        result.Action.ActionType.ShouldBe(SpecialVariables.ActionTypes.TentaclePackage);
        Property(result.Action, SpecialVariables.Action.PackageId).ShouldBe("Acme.Web");
        Property(result.Action, SpecialVariables.Action.PackageFeedId).ShouldBe("301");
        Property(result.Action, SpecialVariables.Action.PackageVersion).ShouldBe("1.2.3");
        Property(result.Action, SpecialVariables.Action.InstallationDirectoryMode).ShouldBe("Versioned");
        result.Action.Properties.ShouldNotContain(p => p.PropertyName == SpecialVariables.Action.CustomInstallationDirectory);
    }

    [Fact]
    public void TentaclePackageMapper_MapsLegacyOfficialPropertiesAndCustomDirectory()
    {
        var mapper = new OctopusTentaclePackageActionMapper();
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-package",
            Name = "Deploy legacy package",
            ActionType = "Octopus.TentaclePackage",
            Properties =
            {
                ["Octopus.Action.Package.NuGetPackageId"] = "Acme.Legacy",
                ["Octopus.Action.Package.NuGetFeedId"] = "Feeds-1-PREFIX-VALUES",
                ["Octopus.Action.Package.PackageVersion"] = "1.0.0",
                ["Octopus.Action.Package.CustomInstallationDirectory"] = "/srv/acme"
            }
        };

        var result = mapper.Map(action, ContextWithFeed());

        result.HasBlockers.ShouldBeFalse();
        Property(result.Action, SpecialVariables.Action.PackageId).ShouldBe("Acme.Legacy");
        Property(result.Action, SpecialVariables.Action.PackageFeedId).ShouldBe("301");
        Property(result.Action, SpecialVariables.Action.PackageVersion).ShouldBe("1.0.0");
        Property(result.Action, SpecialVariables.Action.InstallationDirectoryMode).ShouldBe("Custom");
        Property(result.Action, SpecialVariables.Action.CustomInstallationDirectory).ShouldBe("/srv/acme");
    }

    [Fact]
    public void TentaclePackageMapper_WhenOnlyUniqueSourceIdPrefixFeedMappingExists_AddsBlocker()
    {
        var mapper = new OctopusTentaclePackageActionMapper();
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-package",
            Name = "Deploy package",
            ActionType = "Octopus.TentaclePackage",
            Packages =
            [
                new OctopusActionPackageDto
                {
                    PackageId = "Acme.Web",
                    FeedId = "Feeds-1",
                    Version = "1.2.3"
                }
            ]
        };

        var result = mapper.Map(action, ContextWithFeed());

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.ShouldContain(d => d.Code == OctopusImportActionMappingDiagnosticCodes.MissingFeedMapping);
        result.Action.Properties.ShouldNotContain(p => p.PropertyName == SpecialVariables.Action.PackageFeedId);
    }

    [Fact]
    public void TentaclePackageMapper_WhenPackageReferenceIsMissing_AddsBlocker()
    {
        var mapper = new OctopusTentaclePackageActionMapper();
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-package",
            Name = "Deploy package",
            ActionType = "Octopus.TentaclePackage"
        };

        var result = mapper.Map(action, Context());

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.ShouldContain(d => d.Code == OctopusImportActionMappingDiagnosticCodes.ActionPropertiesOmitted);
    }

    private static OctopusImportActionMappingContext ContextWithFeed()
    {
        var idMap = new OctopusImportIdMap();
        idMap.AddReused(
            Resource("Feeds-1-PREFIX-VALUES", OctopusResourceKind.Feed, "Built-in feed", new OctopusFeedDto()),
            301);

        return new OctopusImportActionMappingContext(idMap, 42);
    }

    private static OctopusImportActionMappingContext Context()
        => new(new OctopusImportIdMap(), 42);

    private static string Property(CreateOrUpdateDeploymentActionModel action, string name)
        => action.Properties.Single(p => p.PropertyName == name).PropertyValue;

    private static OctopusResourceNode Resource(
        string sourceId,
        OctopusResourceKind kind,
        string name,
        object source)
        => new(
            sourceId,
            name,
            kind,
            OctopusDocumentKind.DeploymentProcess,
            $"{sourceId}.json",
            null,
            null,
            false,
            source);
}
