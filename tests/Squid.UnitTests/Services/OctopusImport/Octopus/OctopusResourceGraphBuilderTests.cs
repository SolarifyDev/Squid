using System.Linq;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport.Octopus;

public class OctopusResourceGraphBuilderTests
{
    private readonly OctopusInputExtractor _inputExtractor = new();
    private readonly OctopusManifestInventoryBuilder _inventoryBuilder = new();
    private readonly OctopusResourceGraphBuilder _graphBuilder = new();

    [Fact]
    public async Task Build_CurrentProjectConfiguration_CreatesTypedResourcesReferencesAndDependencies()
    {
        var projectGroupJson = """{"Id":"ProjectGroups-1","Name":"Default"}""";
        var environmentJson = """{"Id":"Environments-1","Name":"Production"}""";
        var lifecycleJson = """
                            {
                                "Id":"Lifecycles-1",
                                "Name":"Default",
                                "Phases":[
                                    {
                                        "Id":"Phase-1",
                                        "Name":"Deploy",
                                        "AutomaticDeploymentTargets":["Environments-1"]
                                    }
                                ]
                            }
                            """;
        var feedJson = """{"Id":"Feeds-1","Name":"Docker","FeedType":"Docker","FeedUri":"https://registry.example"}""";
        var channelJson = """{"Id":"Channels-1","Name":"Default","ProjectId":"Projects-1","LifecycleId":"Lifecycles-1","IsDefault":true}""";
        var settingsJson = """{"Id":"deploymentsettings-Projects-1","ProjectId":"Projects-1"}""";
        var variablesJson = """
                            {
                                "Id":"variableset-Projects-1",
                                "OwnerId":"Projects-1",
                                "OwnerType":"Project",
                                "Variables":[
                                    {
                                        "Id":"Variables-1",
                                        "Name":"Namespace",
                                        "Value":"#{Namespace}",
                                        "Scope":{
                                            "Environment":["Environments-1"],
                                            "Channel":["Channels-1"],
                                            "Action":["Actions-1"],
                                            "Role":["aws-eks-us"]
                                        }
                                    }
                                ]
                            }
                            """;
        var processJson = """
                          {
                              "Id":"deploymentprocess-Projects-1",
                              "OwnerId":"Projects-1",
                              "Steps":[
                                  {
                                      "Id":"Steps-1",
                                      "Name":"Deploy",
                                      "Actions":[
                                          {
                                              "Id":"Actions-1",
                                              "Name":"Deploy containers",
                                              "ActionType":"Octopus.KubernetesDeployContainers",
                                              "Environments":["Environments-1"],
                                              "Channels":["Channels-1"],
                                              "Container":{"FeedId":"Feeds-1"},
                                              "Packages":[{"Id":"Packages-1","FeedId":"Feeds-1"}],
                                              "Properties":{"Octopus.Action.TargetRoles":"aws-eks-us"}
                                          }
                                      ]
                                  }
                              ]
                          }
                          """;
        var projectJson = """
                          {
                              "Id":"Projects-1",
                              "Name":"Project",
                              "VariableSetId":"variableset-Projects-1",
                              "DeploymentProcessId":"deploymentprocess-Projects-1",
                              "DeploymentSettingsId":"deploymentsettings-Projects-1",
                              "ProjectGroupId":"ProjectGroups-1",
                              "LifecycleId":"Lifecycles-1"
                          }
                          """;

        var inventory = await BuildInventoryAsync(
            ("ProjectGroups-1", "ProjectGroup", "ProjectGroups-1.json", projectGroupJson),
            ("Environments-1", "StaticDeploymentEnvironment", "Environments-1.json", environmentJson),
            ("Lifecycles-1", "Lifecycle", "Lifecycles-1.json", lifecycleJson),
            ("Feeds-1", "DockerFeed", "Feeds-1.json", feedJson),
            ("Channels-1", "Channel", "Channels-1.json", channelJson),
            ("deploymentsettings-Projects-1", "DeploymentSettings", "deploymentsettings-Projects-1.json", settingsJson),
            ("variableset-Projects-1", "ProjectVariables", "variableset-Projects-1.json", variablesJson),
            ("deploymentprocess-Projects-1", "DeploymentProcess", "deploymentprocess-Projects-1.json", processJson),
            ("Projects-1", "Project", "Projects-1.json", projectJson));

        var graph = _graphBuilder.Build(inventory);

        graph.Diagnostics.ShouldBeEmpty();
        graph.Resources.Single(r => r.SourceId == "Projects-1").Kind.ShouldBe(OctopusResourceKind.Project);
        graph.Resources.Single(r => r.SourceId == "deploymentprocess-Projects-1").OwnerProjectId.ShouldBe("Projects-1");
        graph.Resources.Single(r => r.SourceId == "Steps-1").ParentSourceId.ShouldBe("deploymentprocess-Projects-1");
        graph.Resources.Single(r => r.SourceId == "Actions-1").ParentSourceId.ShouldBe("Steps-1");
        graph.Resources.Single(r => r.SourceId == "Variables-1").ParentSourceId.ShouldBe("variableset-Projects-1");

        graph.References.ShouldContain(r =>
            r.FromSourceId == "Projects-1" &&
            r.ReferenceKind == OctopusResourceReferenceKind.ProjectGroup &&
            r.ToSourceId == "ProjectGroups-1");
        graph.References.ShouldContain(r =>
            r.FromSourceId == "Actions-1" &&
            r.ReferenceKind == OctopusResourceReferenceKind.Feed &&
            r.ToSourceId == "Feeds-1");
        graph.References.ShouldContain(r =>
            r.FromSourceId == "Variables-1" &&
            r.ReferenceKind == OctopusResourceReferenceKind.TargetRole &&
            r.ToSourceId == "aws-eks-us");
        graph.Dependencies.ShouldContain(d =>
            d.SourceId == "Actions-1" &&
            d.ReferenceKind == OctopusResourceReferenceKind.Feed &&
            d.DependsOnSourceId == "Feeds-1");
        graph.Dependencies.ShouldContain(d =>
            d.SourceId == "deploymentprocess-Projects-1" &&
            d.ReferenceKind == OctopusResourceReferenceKind.Project &&
            d.DependsOnSourceId == "Projects-1");
    }

    [Fact]
    public async Task Build_HistoricalDocuments_MarksDocumentResourcesHistoricalWithoutExpandingSnapshotChildren()
    {
        var snapshotVariablesJson = """
                                    {
                                        "Id":"variableset-Projects-1-s-1-ABC",
                                        "OwnerId":"Projects-1",
                                        "Variables":[{"Id":"Variables-1","Name":"Snapshot"}]
                                    }
                                    """;
        var snapshotProcessJson = """
                                  {
                                      "Id":"deploymentprocess-Projects-1-s-1-ABC",
                                      "OwnerId":"Projects-1",
                                      "Steps":[{"Id":"Steps-1","Actions":[{"Id":"Actions-1"}]}]
                                  }
                                  """;
        var releaseJson = """
                          {
                              "Id":"Releases-1",
                              "ProjectId":"Projects-1",
                              "ChannelId":"Channels-1",
                              "ProjectVariableSetSnapshotId":"variableset-Projects-1-s-1-ABC",
                              "ProjectDeploymentProcessSnapshotId":"deploymentprocess-Projects-1-s-1-ABC"
                          }
                          """;
        var deploymentJson = """{"Id":"Deployments-1","ProjectId":"Projects-1","EnvironmentId":"Environments-1","ReleaseId":"Releases-1","TaskId":"ServerTasks-1"}""";
        var taskJson = """{"Id":"ServerTasks-1","ProjectId":"Projects-1","EnvironmentId":"Environments-1"}""";

        var inventory = await BuildInventoryAsync(
            ("variableset-Projects-1-s-1-ABC", "ProjectVariables", "variableset-Projects-1-s-1-ABC.json", snapshotVariablesJson),
            ("deploymentprocess-Projects-1-s-1-ABC", "DeploymentProcess", "deploymentprocess-Projects-1-s-1-ABC.json", snapshotProcessJson),
            ("Releases-1", "Release", "Releases-1.json", releaseJson),
            ("Deployments-1", "Deployment", "Deployments-1.json", deploymentJson),
            ("ServerTasks-1", "ServerTask", "ServerTasks-1.json", taskJson));

        var graph = _graphBuilder.Build(inventory);

        graph.Resources.Single(r => r.SourceId == "variableset-Projects-1-s-1-ABC").IsHistorical.ShouldBeTrue();
        graph.Resources.Single(r => r.SourceId == "deploymentprocess-Projects-1-s-1-ABC").IsHistorical.ShouldBeTrue();
        graph.Resources.ShouldNotContain(r => r.SourceId == "Variables-1");
        graph.Resources.ShouldNotContain(r => r.SourceId == "Steps-1");
        graph.Resources.ShouldNotContain(r => r.SourceId == "Actions-1");
        graph.Resources.Single(r => r.SourceId == "Releases-1").IsHistorical.ShouldBeTrue();
        graph.Resources.Single(r => r.SourceId == "Deployments-1").IsHistorical.ShouldBeTrue();
        graph.Resources.Single(r => r.SourceId == "ServerTasks-1").IsHistorical.ShouldBeTrue();
        graph.References.ShouldContain(r =>
            r.FromSourceId == "Releases-1" &&
            r.ReferenceKind == OctopusResourceReferenceKind.VariableSetSnapshot &&
            r.ToSourceId == "variableset-Projects-1-s-1-ABC");
    }

    [Fact]
    public async Task Build_DuplicateChildIdsAcrossHistoricalSnapshots_DoesNotBlockExtraction()
    {
        const string firstSnapshotJson = """
                                         {
                                             "Id":"variableset-Projects-1-s-1-ABC",
                                             "OwnerId":"Projects-1",
                                             "Variables":[{"Id":"Variables-Reused","Name":"Snapshot variable"}]
                                         }
                                         """;
        const string secondSnapshotJson = """
                                          {
                                              "Id":"variableset-Projects-1-s-2-DEF",
                                              "OwnerId":"Projects-1",
                                              "Variables":[{"Id":"Variables-Reused","Name":"Snapshot variable"}]
                                          }
                                          """;
        const string firstProcessSnapshotJson = """
                                                {
                                                    "Id":"deploymentprocess-Projects-1-s-1-ABC",
                                                    "OwnerId":"Projects-1",
                                                    "Steps":[{"Id":"Steps-Reused","Actions":[{"Id":"Actions-Reused"}]}]
                                                }
                                                """;
        const string secondProcessSnapshotJson = """
                                                 {
                                                     "Id":"deploymentprocess-Projects-1-s-2-DEF",
                                                     "OwnerId":"Projects-1",
                                                     "Steps":[{"Id":"Steps-Reused","Actions":[{"Id":"Actions-Reused"}]}]
                                                 }
                                                 """;

        var inventory = await BuildInventoryAsync(
            ("variableset-Projects-1-s-1-ABC", "ProjectVariables", "variableset-Projects-1-s-1-ABC.json", firstSnapshotJson),
            ("variableset-Projects-1-s-2-DEF", "ProjectVariables", "variableset-Projects-1-s-2-DEF.json", secondSnapshotJson),
            ("deploymentprocess-Projects-1-s-1-ABC", "DeploymentProcess", "deploymentprocess-Projects-1-s-1-ABC.json", firstProcessSnapshotJson),
            ("deploymentprocess-Projects-1-s-2-DEF", "DeploymentProcess", "deploymentprocess-Projects-1-s-2-DEF.json", secondProcessSnapshotJson));

        var graph = _graphBuilder.Build(inventory);

        graph.Diagnostics.ShouldNotContain(d => d.Code == OctopusInputExtractionDiagnosticCodes.GraphDuplicateSourceId);
        graph.Resources.Count(r => r.Kind == OctopusResourceKind.VariableSetSnapshot).ShouldBe(2);
        graph.Resources.Count(r => r.Kind == OctopusResourceKind.DeploymentProcessSnapshot).ShouldBe(2);
        graph.Resources.ShouldNotContain(r => r.SourceId == "Variables-Reused");
        graph.Resources.ShouldNotContain(r => r.SourceId == "Steps-Reused");
        graph.Resources.ShouldNotContain(r => r.SourceId == "Actions-Reused");
    }

    [Fact]
    public async Task Build_CurrentDocumentOverridesHistoricalDuplicateSourceIdWithoutBlocker()
    {
        const string historicalProcessJson = """
                                             {
                                                 "Id":"deploymentprocess-Projects-1",
                                                 "OwnerId":"Projects-1",
                                                 "Steps":[]
                                             }
                                             """;
        const string currentProcessJson = """
                                          {
                                              "Id":"deploymentprocess-Projects-1",
                                              "OwnerId":"Projects-1",
                                              "Steps":[
                                                  {
                                                      "Id":"Steps-1",
                                                      "Name":"Deploy",
                                                      "Actions":[]
                                                  }
                                              ]
                                          }
                                          """;

        var inventory = await BuildInventoryAsync(
            ("deploymentprocess-Projects-1-s-1-ABC", "DeploymentProcess", "deploymentprocess-Projects-1-s-1-ABC.json", historicalProcessJson),
            ("deploymentprocess-Projects-1", "DeploymentProcess", "deploymentprocess-Projects-1.json", currentProcessJson));

        var graph = _graphBuilder.Build(inventory);

        graph.Diagnostics.ShouldNotContain(d => d.Code == OctopusInputExtractionDiagnosticCodes.GraphDuplicateSourceId);
        graph.Resources.Count(r => r.SourceId == "deploymentprocess-Projects-1").ShouldBe(1);
        graph.Resources.Single(r => r.SourceId == "deploymentprocess-Projects-1").IsHistorical.ShouldBeFalse();
        graph.Resources.ShouldContain(r => r.SourceId == "Steps-1");
    }

    [Fact]
    public async Task Build_MalformedTypedDocument_AddsBlockerWithoutSourceValues()
    {
        var sourceValue = "source-value-that-must-not-leak";
        var malformedProjectJson = "{\"Id\":\"Projects-1\",\"Name\":\"" + sourceValue + "\",\"IncludedLibraryVariableSetIds\":{}}";
        var inventory = await BuildInventoryAsync(("Projects-1", "Project", "Projects-1.json", malformedProjectJson));

        var graph = _graphBuilder.Build(inventory);

        var diagnostic = graph.Diagnostics.Single(d => d.Code == OctopusInputExtractionDiagnosticCodes.GraphDocumentMalformed);
        diagnostic.SourcePath.ShouldBe("Projects-1.json");
        diagnostic.SourceId.ShouldBe("Projects-1");
        diagnostic.Message.ShouldNotContain(sourceValue);
        graph.HasBlockers.ShouldBeTrue();
    }

    [Fact]
    public async Task Build_DuplicateNestedSourceId_AddsBlocker()
    {
        var processJson = """
                          {
                              "Id":"deploymentprocess-Projects-1",
                              "OwnerId":"Projects-1",
                              "Steps":[
                                  {"Id":"Steps-1","Actions":[{"Id":"Actions-1"}]},
                                  {"Id":"Steps-1","Actions":[{"Id":"Actions-2"}]}
                              ]
                          }
                          """;
        var inventory = await BuildInventoryAsync(("deploymentprocess-Projects-1", "DeploymentProcess", "deploymentprocess-Projects-1.json", processJson));

        var graph = _graphBuilder.Build(inventory);

        var diagnostic = graph.Diagnostics.Single(d => d.Code == OctopusInputExtractionDiagnosticCodes.GraphDuplicateSourceId);
        diagnostic.SourceId.ShouldBe("Steps-1");
        diagnostic.SourcePath.ShouldBe("deploymentprocess-Projects-1.json");
        graph.HasBlockers.ShouldBeTrue();
    }

    [Fact]
    public async Task Build_MissingReferenceTarget_KeepsTypedReferenceWithoutBlocker()
    {
        var projectJson = """{"Id":"Projects-1","Name":"Project","LifecycleId":"Lifecycles-Missing"}""";
        var inventory = await BuildInventoryAsync(("Projects-1", "Project", "Projects-1.json", projectJson));

        var graph = _graphBuilder.Build(inventory);

        graph.Diagnostics.ShouldBeEmpty();
        graph.References.Single(r => r.ReferenceKind == OctopusResourceReferenceKind.Lifecycle).ToSourceId.ShouldBe("Lifecycles-Missing");
    }

    [Fact]
    public async Task Build_WorkerPoolDocument_AddsWorkerPoolResourceWithoutBlocker()
    {
        var workerPoolJson = """{"Id":"WorkerPools-1","Name":"Default Worker Pool","DocumentType":"WorkerPool"}""";
        var inventory = await BuildInventoryAsync(("WorkerPools-1", "WorkerPool", "WorkerPools-1.json", workerPoolJson));

        var graph = _graphBuilder.Build(inventory);

        graph.Diagnostics.ShouldNotContain(d => d.Severity == Squid.Message.Enums.OctopusImport.OctopusImportCompatibilitySeverity.Blocker);
        var resource = graph.Resources.Single();
        resource.SourceId.ShouldBe("WorkerPools-1");
        resource.Kind.ShouldBe(OctopusResourceKind.WorkerPool);
        resource.DocumentKind.ShouldBe(OctopusDocumentKind.WorkerPool);
    }

    private async Task<OctopusManifestInventoryResult> BuildInventoryAsync(params (string Id, string DocumentType, string DocumentSource, string Json)[] documents)
    {
        var archiveEntries = new List<OctopusExtractedArchiveEntry>
        {
            new("manifest.json", Encoding.UTF8.GetBytes(BuildManifestJson(documents)))
        };

        archiveEntries.AddRange(documents.Select(d => new OctopusExtractedArchiveEntry(d.DocumentSource, Encoding.UTF8.GetBytes(d.Json))));

        var extractionResult = await _inputExtractor.ExtractJsonEntriesAsync(archiveEntries);
        return _inventoryBuilder.Build(extractionResult);
    }

    private static string BuildManifestJson((string Id, string DocumentType, string DocumentSource, string Json)[] documents)
    {
        var manifest = new
        {
            SchemaVersions = Array.Empty<string>(),
            Entries = documents.Select(d => new
            {
                d.Id,
                Name = d.Id,
                d.DocumentType,
                ExportType = "FullDocument",
                d.DocumentSource,
                ParentId = (string)null,
                Hash = Sha1(d.Json)
            })
        };

        return JsonSerializer.Serialize(manifest);
    }

    private static string Sha1(string value)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
