using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Archive-backed coverage for the three extended Octopus action mappings.
/// The action payloads follow the official Octopus Library action templates at
/// https://github.com/OctopusDeploy/Library/commit/59524130b4285338a3d3f9351bf5c54cf57e6c99
/// and the Calamari variable names used by the Helm action.
/// </summary>
public class OctopusImportExtendedActionFixtureIntegrationTests : TestBase
{
    private const int SpaceId = 7;

    public OctopusImportExtendedActionFixtureIntegrationTests()
        : base("OctopusImportExtendedActionFixture", "squid_it_octopus_import_extended_action_fixture")
    {
    }

    [Fact]
    public async Task ArchiveImport_WithOfficialRawYamlHelmAndTentaclePackageShapes_PersistsMappedActions()
    {
        var sessionId = Guid.NewGuid();
        var archivePath = CreateOfficialActionShapeArchive();

        try
        {
            await Run<SquidDbContext, IOctopusImportPlanningPipeline, IOctopusImportConfirmationOrchestrator>(
                async (db, pipeline, orchestrator) =>
                {
                    db.Set<OctopusImportSession>().Add(CreateSession(sessionId));
                    await db.SaveChangesAsync();
                    db.ChangeTracker.Clear();

                    var snapshot = await pipeline.BuildPreviewAsync(archivePath, SpaceId);

                    snapshot.Graph.Diagnostics.ShouldNotContain(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
                    snapshot.DependencyPlan.Diagnostics.ShouldNotContain(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
                    snapshot.PreviewPlan.HasBlockers.ShouldBeFalse();

                    var result = await orchestrator.ConfirmAsync(new OctopusImportConfirmationRequest(
                        sessionId,
                        SpaceId,
                        snapshot.Graph,
                        snapshot.DependencyPlan,
                        snapshot.PreviewPlan));

                    result.State.ShouldBe(
                        OctopusImportSessionState.Succeeded,
                        string.Join(
                            System.Environment.NewLine,
                            result.Result.Diagnostics
                                .Concat(result.Result.Resources.SelectMany(resource => resource.Diagnostics))
                                .Select(diagnostic =>
                                    $"{diagnostic.Severity} {diagnostic.Code} [{diagnostic.SourceId}] {diagnostic.Message}")));
                    result.Result.Succeeded.ShouldBeTrue();

                    var project = await db.Set<Project>()
                        .SingleAsync(p => p.SpaceId == SpaceId && p.Name == "Official action shape fixture");
                    var feed = await db.Set<ExternalFeed>()
                        .SingleAsync(candidate => candidate.SpaceId == SpaceId && candidate.Name == "Fixture NuGet feed");
                    var step = await db.Set<DeploymentStep>()
                        .SingleAsync(candidate => candidate.ProcessId == project.DeploymentProcessId);
                    var actions = await db.Set<DeploymentAction>()
                        .Where(candidate => candidate.StepId == step.Id)
                        .OrderBy(candidate => candidate.ActionOrder)
                        .ToListAsync();
                    var actionIds = actions.Select(action => action.Id).ToList();
                    var properties = await db.Set<DeploymentActionProperty>()
                        .Where(property => actionIds.Contains(property.ActionId))
                        .ToListAsync();

                    actions.Count.ShouldBe(3);

                    var rawYamlAction = actions.Single(action => action.Name == "Apply official HPA manifest");
                    rawYamlAction.ActionType.ShouldBe(SpecialVariables.ActionTypes.KubernetesDeployRawYaml);
                    Property(properties, rawYamlAction.Id, "Squid.Action.KubernetesYaml.InlineYaml")
                        .ShouldContain("kind: HorizontalPodAutoscaler");
                    Property(properties, rawYamlAction.Id, "Squid.Action.KubernetesYaml.InlineYaml")
                        .ShouldContain("#{HorizontalPodAutoscalerName}");

                    var helmAction = actions.Single(action => action.Name == "Upgrade official Helm chart");
                    helmAction.ActionType.ShouldBe(SpecialVariables.ActionTypes.HelmChartUpgrade);
                    Property(properties, helmAction.Id, "Squid.Action.Helm.ReleaseName").ShouldBe("fixture-web");
                    Property(properties, helmAction.Id, "Squid.Action.KubernetesContainers.Namespace").ShouldBe("fixture-production");
                    Property(properties, helmAction.Id, "Squid.Action.Helm.ChartPath").ShouldBe("charts/fixture-web");
                    Property(properties, helmAction.Id, "Squid.Action.Helm.ValueSources")
                        .ShouldContain("\"Type\":\"InlineYaml\"");
                    Property(properties, helmAction.Id, "Squid.Action.Package.PackageId").ShouldBe("Acme.Fixture.Chart");
                    Property(properties, helmAction.Id, "Squid.Action.Package.FeedId").ShouldBe(feed.Id.ToString());
                    Property(properties, helmAction.Id, "Squid.Action.Package.PackageVersion").ShouldBe("3.1.4");

                    var packageAction = actions.Single(action => action.Name == "Deploy official Tentacle package");
                    packageAction.ActionType.ShouldBe(SpecialVariables.ActionTypes.TentaclePackage);
                    Property(properties, packageAction.Id, "Squid.Action.Package.PackageId").ShouldBe("Acme.Fixture.Package");
                    Property(properties, packageAction.Id, "Squid.Action.Package.FeedId").ShouldBe(feed.Id.ToString());
                    Property(properties, packageAction.Id, "Squid.Action.Package.PackageVersion").ShouldBe("1.2.3");
                    Property(properties, packageAction.Id, "Squid.Action.Package.InstallationDirectoryMode").ShouldBe("Custom");
                    Property(properties, packageAction.Id, "Squid.Action.Package.CustomInstallationDirectory").ShouldBe("/srv/acme");

                    result.Result.IdMappings.Single(mapping =>
                        mapping.SourceType == OctopusResourceKind.DeploymentAction.ToString()
                        && mapping.SourceId == "Actions-RawYaml").DestinationId.ShouldBe(rawYamlAction.Id);
                    result.Result.IdMappings.Single(mapping =>
                        mapping.SourceType == OctopusResourceKind.DeploymentAction.ToString()
                        && mapping.SourceId == "Actions-Helm").DestinationId.ShouldBe(helmAction.Id);
                    result.Result.IdMappings.Single(mapping =>
                        mapping.SourceType == OctopusResourceKind.DeploymentAction.ToString()
                        && mapping.SourceId == "Actions-Package").DestinationId.ShouldBe(packageAction.Id);
                });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    private static OctopusImportSession CreateSession(Guid sessionId)
        => new()
        {
            SessionId = sessionId,
            DestinationSpaceId = SpaceId,
            OwnerUserId = CurrentUsers.InternalUser.Id,
            State = OctopusImportSessionState.Validated.ToString(),
            SourceSummaryJson = "{}",
            DataVersion = Guid.NewGuid().ToByteArray(),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            LastStateChangedAt = DateTimeOffset.UtcNow
        };

    private static string Property(
        IReadOnlyCollection<DeploymentActionProperty> properties,
        int actionId,
        string propertyName)
        => properties.Single(property =>
            property.ActionId == actionId
            && string.Equals(property.PropertyName, propertyName, StringComparison.OrdinalIgnoreCase))
            .PropertyValue;

    private static string CreateOfficialActionShapeArchive()
    {
        var documents = new[]
        {
            Document(
                "ProjectGroups-1",
                "ProjectGroup",
                "ProjectGroups-1.json",
                """{"Id":"ProjectGroups-1","Name":"Extended action fixture group","Slug":"extended-action-fixture-group"}"""),
            Document(
                "Environments-1",
                "StaticDeploymentEnvironment",
                "Environments-1.json",
                """{"Id":"Environments-1","Name":"Fixture Production","Slug":"fixture-production"}"""),
            Document(
                "Lifecycles-1",
                "Lifecycle",
                "Lifecycles-1.json",
                """
                {
                  "Id":"Lifecycles-1",
                  "Name":"Extended action fixture lifecycle",
                  "Slug":"extended-action-fixture-lifecycle",
                  "Phases":[{
                    "Id":"Phases-1",
                    "Name":"Production",
                    "AutomaticDeploymentTargets":["Environments-1"]
                  }]
                }
                """),
            Document(
                "Feeds-1",
                "NuGetFeed",
                "Feeds-1.json",
                """
                {
                  "Id":"Feeds-1",
                  "Name":"Fixture NuGet feed",
                  "Slug":"fixture-nuget-feed",
                  "FeedType":"NuGet",
                  "FeedUri":"https://api.nuget.org/v3/index.json"
                }
                """),
            Document(
                "Projects-1",
                "Project",
                "Projects-1.json",
                """
                {
                  "Id":"Projects-1",
                  "Name":"Official action shape fixture",
                  "Slug":"official-action-shape-fixture",
                  "ProjectGroupId":"ProjectGroups-1",
                  "LifecycleId":"Lifecycles-1",
                  "VariableSetId":"variableset-Projects-1",
                  "DeploymentProcessId":"deploymentprocess-Projects-1"
                }
                """),
            Document(
                "variableset-Projects-1",
                "ProjectVariables",
                "variableset-Projects-1.json",
                """
                {
                  "Id":"variableset-Projects-1",
                  "OwnerId":"Projects-1",
                  "OwnerType":"Project",
                  "Variables":[
                    {"Id":"Variables-HpaName","Name":"HorizontalPodAutoscalerName","Value":"fixture-hpa","Type":"String"},
                    {"Id":"Variables-HpaTargetApi","Name":"HorizontalPodAutoscalerTargetApiVersion","Value":"apps/v1","Type":"String"},
                    {"Id":"Variables-HpaTargetKind","Name":"HorizontalPodAutoscalerTargetKind","Value":"Deployment","Type":"String"},
                    {"Id":"Variables-HpaTargetName","Name":"HorizontalPodAutoscalerTargetName","Value":"fixture-web","Type":"String"},
                    {"Id":"Variables-HpaMin","Name":"HorizontalPodAutoscalerMinReplicas","Value":"2","Type":"String"},
                    {"Id":"Variables-HpaMax","Name":"HorizontalPodAutoscalerMaxReplicas","Value":"5","Type":"String"},
                    {"Id":"Variables-HpaMetric","Name":"HorizontalPodAutoscalerMetricName","Value":"requests-per-second","Type":"String"},
                    {"Id":"Variables-HpaMetricType","Name":"HorizontalPodAutoscalerMetricType","Value":"AverageValue","Type":"String"},
                    {"Id":"Variables-HpaAverageValue","Name":"HorizontalPodAutoscalerAverageValue","Value":"100m","Type":"String"}
                  ]
                }
                """),
            Document(
                "deploymentprocess-Projects-1",
                "DeploymentProcess",
                "deploymentprocess-Projects-1.json",
                """
                {
                  "Id":"deploymentprocess-Projects-1",
                  "OwnerId":"Projects-1",
                  "Version":1,
                  "Steps":[{
                    "Id":"Steps-1",
                    "Name":"Official action shapes",
                    "Condition":"Success",
                    "StartTrigger":"StartAfterPrevious",
                    "Actions":[
                      {
                        "Id":"Actions-RawYaml",
                        "Name":"Apply official HPA manifest",
                        "ActionType":"Octopus.KubernetesDeployRawYaml",
                        "IsRequired":true,
                        "Properties":{
                          "Octopus.Action.Script.ScriptSource":"Inline",
                          "Octopus.Action.KubernetesContainers.CustomResourceYaml":"# Official Octopus Library k8s-hpa-external-metrics template\napiVersion: autoscaling/v2beta2\nkind: HorizontalPodAutoscaler\nmetadata:\n  name: #{HorizontalPodAutoscalerName}\nspec:\n  scaleTargetRef:\n    apiVersion: #{HorizontalPodAutoscalerTargetApiVersion}\n    kind: #{HorizontalPodAutoscalerTargetKind}\n    name: #{HorizontalPodAutoscalerTargetName}\n  minReplicas: #{HorizontalPodAutoscalerMinReplicas}\n  maxReplicas: #{HorizontalPodAutoscalerMaxReplicas}\n  metrics:\n  - type: External\n    external:\n      metric:\n        name: #{HorizontalPodAutoscalerMetricName}\n      target:\n        type: #{HorizontalPodAutoscalerMetricType}\n        averageValue: #{HorizontalPodAutoscalerAverageValue}"
                        }
                      },
                      {
                        "Id":"Actions-Helm",
                        "Name":"Upgrade official Helm chart",
                        "ActionType":"Octopus.HelmChartUpgrade",
                        "IsRequired":true,
                        "Packages":[{
                          "Id":"Packages-Helm",
                          "Name":"fixture-web",
                          "PackageId":"Acme.Fixture.Chart",
                          "FeedId":"Feeds-1",
                          "Version":"3.1.4"
                        }],
                        "Properties":{
                          "Octopus.Action.Helm.ReleaseName":"fixture-web",
                          "Octopus.Action.Helm.Namespace":"fixture-production",
                          "Octopus.Action.Helm.ChartDirectory":"charts/fixture-web",
                          "Octopus.Action.Helm.CustomHelmExecutable":"/usr/local/bin/helm",
                          "Octopus.Action.Helm.ResetValues":"False",
                          "Octopus.Action.Helm.AdditionalArgs":"--atomic",
                          "Octopus.Action.Helm.Timeout":"5m",
                          "Octopus.Action.Helm.ClientVersion":"V3",
                          "Octopus.Action.Helm.TemplateValuesSources":"[{\"Type\":\"InlineYaml\",\"Value\":\"replicaCount: 3\"},{\"Type\":\"KeyValues\",\"Value\":{\"image.tag\":\"v3\",\"enabled\":false}}]"
                        }
                      },
                      {
                        "Id":"Actions-Package",
                        "Name":"Deploy official Tentacle package",
                        "ActionType":"Octopus.TentaclePackage",
                        "IsRequired":true,
                        "Packages":[{
                          "Id":"Packages-Package",
                          "Name":"",
                          "PackageId":"Acme.Fixture.Package",
                          "FeedId":"Feeds-1",
                          "AcquisitionLocation":"Server",
                          "Version":"1.2.3"
                        }],
                        "Properties":{
                          "Octopus.Action.Package.CustomInstallationDirectory":"/srv/acme",
                          "Octopus.Action.Package.AutomaticallyRunConfigurationTransformationFiles":"True",
                          "Octopus.Action.Package.AutomaticallyUpdateAppSettingsAndConnectionStrings":"True"
                        }
                      }
                    ]
                  }]
                }
                """)
        };

        var path = Path.Combine(Path.GetTempPath(), $"squid-octopus-import-extended-action-{Guid.NewGuid():N}.zip");
        var manifest = System.Text.Json.JsonSerializer.Serialize(new
        {
            SchemaVersions = Array.Empty<string>(),
            Entries = documents.Select(document => new
            {
                document.Id,
                Name = document.Id,
                document.DocumentType,
                ExportType = "FullDocument",
                document.DocumentSource,
                ParentId = (string)null,
                Hash = Sha1(document.Json)
            })
        });

        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        WriteArchiveEntry(archive, "manifest.json", manifest);

        foreach (var document in documents)
            WriteArchiveEntry(archive, document.DocumentSource, document.Json);

        return path;
    }

    private static (string Id, string DocumentType, string DocumentSource, string Json) Document(
        string id,
        string documentType,
        string documentSource,
        string json)
        => (id, documentType, documentSource, json);

    private static void WriteArchiveEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static string Sha1(string value)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
