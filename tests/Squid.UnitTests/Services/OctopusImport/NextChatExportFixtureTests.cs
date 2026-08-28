using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Constants;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class NextChatExportFixtureTests
{
    [Fact]
    public async Task RealFixture_MapsCurrentDeploymentProcessWithoutConfirmationBlockers()
    {
        var folder = FindFixtureFolder("Next Chat Export");
        var extractor = new OctopusInputExtractor();
        var inventoryBuilder = new OctopusManifestInventoryBuilder();
        var graphBuilder = new OctopusResourceGraphBuilder();

        var extraction = await extractor.ExtractFolderAsync(folder);
        var inventory = inventoryBuilder.Build(extraction);
        var graph = graphBuilder.Build(inventory);
        var dependencyPlan = new OctopusImportDependencyPlanner().BuildCurrentConfigurationPlan(graph);
        var currentProcess = graph.Resources.Single(resource =>
            resource.Kind == OctopusResourceKind.DeploymentProcess && !resource.IsHistorical);
        var idMap = BuildFixtureIdMap(graph);

        var processMapping = CreateProcessMapper().MapToCreateStepCommands(currentProcess, idMap, 1);

        extraction.Diagnostics.ShouldBeEmpty();
        inventory.Diagnostics.ShouldBeEmpty();
        graph.Diagnostics.ShouldBeEmpty();
        dependencyPlan.Diagnostics.ShouldBeEmpty();
        processMapping.HasBlockers.ShouldBeFalse();
        processMapping.Diagnostics.Select(d => d.Code)
            .ShouldNotContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.MissingProcessMapping);
        processMapping.Diagnostics.Select(d => d.Code)
            .ShouldNotContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.UnsupportedActionCondition);
    }

    [Fact]
    public async Task RedactedFixture_MapsCurrentConfigurationAndExcludesHistoricalResourcesWithoutLeakingSecrets()
    {
        var entries = RedactedNextChatExport.BuildEntries();
        var extractor = new OctopusInputExtractor();
        var inventoryBuilder = new OctopusManifestInventoryBuilder();
        var graphBuilder = new OctopusResourceGraphBuilder();

        var extraction = await extractor.ExtractJsonEntriesAsync(entries);
        var inventory = inventoryBuilder.Build(extraction);
        var graph = graphBuilder.Build(inventory);
        var dependencyPlan = new OctopusImportDependencyPlanner().BuildCurrentConfigurationPlan(graph);
        var preview = new OctopusImportPreviewPlanner().BuildPreviewPlan(
            dependencyPlan,
            new OctopusImportConflictDiscoveryResult([]));

        extraction.Diagnostics.ShouldBeEmpty();
        inventory.Diagnostics.ShouldBeEmpty();
        inventory.Items.Count.ShouldBe(1416);
        inventory.Items.Count(item => item.Classification.Kind == OctopusDocumentKind.DeploymentProcess).ShouldBe(1);
        inventory.Items.Count(item => item.Classification.Kind == OctopusDocumentKind.DeploymentProcessSnapshot).ShouldBe(14);
        inventory.Items.Count(item => item.Classification.Kind == OctopusDocumentKind.VariableSet).ShouldBe(1);
        inventory.Items.Count(item => item.Classification.Kind == OctopusDocumentKind.VariableSetSnapshot).ShouldBe(37);
        inventory.Items.Count(item => item.Classification.Kind == OctopusDocumentKind.Release).ShouldBe(348);
        inventory.Items.Count(item => item.Classification.Kind == OctopusDocumentKind.Deployment).ShouldBe(502);
        inventory.Items.Count(item => item.Classification.Kind == OctopusDocumentKind.ServerTask).ShouldBe(502);

        graph.Diagnostics.ShouldBeEmpty();
        var currentProcess = graph.Resources.Single(resource =>
            resource.Kind == OctopusResourceKind.DeploymentProcess);
        var currentVariableSet = graph.Resources.Single(resource =>
            resource.Kind == OctopusResourceKind.VariableSet);
        currentProcess.GetSource<OctopusDeploymentProcessDto>().Steps.Count.ShouldBe(3);
        currentVariableSet.GetSource<OctopusVariableSetDto>().Variables.Count.ShouldBe(44);

        dependencyPlan.OrderedResources.Count(resource => resource.Kind == OctopusResourceKind.DeploymentProcess).ShouldBe(1);
        dependencyPlan.OrderedResources.Count(resource => resource.Kind == OctopusResourceKind.VariableSet).ShouldBe(1);
        dependencyPlan.OutOfScopeResources.Count(resource => resource.Kind == OctopusResourceKind.DeploymentProcessSnapshot).ShouldBe(14);
        dependencyPlan.OutOfScopeResources.Count(resource => resource.Kind == OctopusResourceKind.VariableSetSnapshot).ShouldBe(37);
        dependencyPlan.OutOfScopeResources.Count(resource => resource.Kind == OctopusResourceKind.Release).ShouldBe(348);
        dependencyPlan.OutOfScopeResources.Count(resource => resource.Kind == OctopusResourceKind.Deployment).ShouldBe(502);
        dependencyPlan.OutOfScopeResources.Count(resource => resource.Kind == OctopusResourceKind.ServerTask).ShouldBe(502);

        var idMap = BuildFixtureIdMap(graph);
        var processMapping = CreateProcessMapper().MapToCreateStepCommands(currentProcess, idMap, 7);

        processMapping.HasBlockers.ShouldBeFalse();
        processMapping.Steps.Count.ShouldBe(3);
        processMapping.Steps.Select(step => step.CreateCommand.Step.Name)
            .ShouldBe(["Approval-Solar", "Deploy Kubernetes resources", "Deploy Kubernetes Ingress"]);
        processMapping.Steps.SelectMany(step => step.Actions).Count().ShouldBe(3);

        var validation = new OctopusImportPreviewValidator().Validate(
            graph,
            dependencyPlan,
            new OctopusImportConflictDiscoveryResult([]),
            preview);

        validation.Diagnostics.Any(d =>
            d.Code == OctopusImportPreviewDiagnosticCodes.MissingTargetRole &&
            d.Message.Contains("aws-eks-us", StringComparison.Ordinal)).ShouldBeTrue();
        preview.Resources.Count(resource =>
            resource.PreviewAction == OctopusImportPreviewAction.Skip).ShouldBe(dependencyPlan.OutOfScopeResources.Count);
        preview.RequiredInputs.ShouldContain(input =>
            input.Kind == OctopusImportRequiredInputKind.SensitiveVariableValue &&
            input.Name == "ApiKey" &&
            input.HasSourceValue);

        var previewJson = JsonSerializer.Serialize(preview);
        var validationJson = JsonSerializer.Serialize(validation);
        previewJson.ShouldNotContain(RedactedNextChatExport.VariableSecret);
        previewJson.ShouldNotContain(RedactedNextChatExport.FeedUsername);
        previewJson.ShouldNotContain(RedactedNextChatExport.FeedPassword);
        validationJson.ShouldNotContain(RedactedNextChatExport.VariableSecret);
        validationJson.ShouldNotContain(RedactedNextChatExport.FeedUsername);
        validationJson.ShouldNotContain(RedactedNextChatExport.FeedPassword);
    }

    private static OctopusImportIdMap BuildFixtureIdMap(OctopusResourceGraph graph)
    {
        var idMap = new OctopusImportIdMap();
        var destinationId = 100;

        foreach (var resource in graph.Resources
                     .Where(resource => !resource.IsHistorical)
                     .OrderBy(resource => resource.Kind)
                     .ThenBy(resource => resource.SourceId, StringComparer.OrdinalIgnoreCase))
        {
            idMap.AddReused(resource, destinationId++);
        }

        return idMap;
    }

    private static OctopusImportDeploymentProcessMapper CreateProcessMapper()
        => new(new OctopusImportActionMapperRegistry(
        [
            new OctopusKubernetesDeployContainersActionMapper(),
            new OctopusKubernetesDeployIngressActionMapper(),
            new OctopusManualActionMapper()
        ]));

    private static string FindFixtureFolder(string folderName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, folderName);
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException($"Could not find '{folderName}' from '{AppContext.BaseDirectory}'.");
    }

    private static class RedactedNextChatExport
    {
        public const string VariableSecret = "fixture-variable-api-key-secret";
        public const string FeedUsername = "fixture-feed-user";
        public const string FeedPassword = "fixture-feed-password-secret";

        public static IReadOnlyList<OctopusExtractedArchiveEntry> BuildEntries()
        {
            var documents = new List<FixtureDocument>
            {
                Document(
                    "ProjectGroups-1",
                    "ProjectGroup",
                    "ProjectGroups-1.json",
                    """{"Id":"ProjectGroups-1","Name":"Next Chat Group"}"""),
                Document(
                    "Environments-1",
                    "StaticDeploymentEnvironment",
                    "Environments-1.json",
                    """{"Id":"Environments-1","Name":"Development"}"""),
                Document(
                    "Environments-2",
                    "StaticDeploymentEnvironment",
                    "Environments-2.json",
                    """{"Id":"Environments-2","Name":"Staging"}"""),
                Document(
                    "Environments-3",
                    "StaticDeploymentEnvironment",
                    "Environments-3.json",
                    """{"Id":"Environments-3","Name":"Production"}"""),
                Document(
                    "Lifecycles-1",
                    "Lifecycle",
                    "Lifecycles-1.json",
                    """{"Id":"Lifecycles-1","Name":"Default","Phases":[{"Id":"Phase-1","Name":"Deploy","AutomaticDeploymentTargets":["Environments-1","Environments-2","Environments-3"]}]}"""),
                Document(
                    "Feeds-1",
                    "DockerFeed",
                    "Feeds-1.json",
                    $$"""{"Id":"Feeds-1","Name":"Docker Hub","FeedType":"Docker","FeedUri":"https://registry.example","Username":"{{FeedUsername}}","Password":"{{FeedPassword}}"}"""),
                Document(
                    "Teams-1",
                    "ConfigurableTeam",
                    "Teams-1.json",
                    """{"Id":"Teams-1","Name":"Release approvers"}"""),
                Document(
                    "deploymentsettings-Projects-1",
                    "DeploymentSettings",
                    "deploymentsettings-Projects-1.json",
                    """{"Id":"deploymentsettings-Projects-1","Name":"Default","ProjectId":"Projects-1"}"""),
                Document(
                    "Channels-1",
                    "Channel",
                    "Channels-1.json",
                    """{"Id":"Channels-1","Name":"Default","ProjectId":"Projects-1","LifecycleId":"Lifecycles-1","IsDefault":true}"""),
                Document(
                    "Projects-1",
                    "Project",
                    "Projects-1.json",
                    """{"Id":"Projects-1","Name":"Next Chat","VariableSetId":"variableset-Projects-1","DeploymentProcessId":"deploymentprocess-Projects-1","DeploymentSettingsId":"deploymentsettings-Projects-1","ProjectGroupId":"ProjectGroups-1","LifecycleId":"Lifecycles-1"}"""),
                Document(
                    "variableset-Projects-1",
                    "ProjectVariables",
                    "variableset-Projects-1.json",
                    JsonSerializer.Serialize(BuildCurrentVariableSet())),
                Document(
                    "deploymentprocess-Projects-1",
                    "DeploymentProcess",
                    "deploymentprocess-Projects-1.json",
                    JsonSerializer.Serialize(BuildCurrentProcess())),
                Document(
                    "ActionTemplates-1",
                    "ActionTemplate",
                    "ActionTemplates-1.json",
                    """{"Id":"ActionTemplates-1","Name":"Redacted action template"}""")
            };

            for (var index = 1; index <= 37; index++)
            {
                var id = $"variableset-Projects-1-s-{index}-redacted";
                documents.Add(Document(
                    id,
                    "ProjectVariables",
                    $"{id}.json",
                    $$"""{"Id":"{{id}}","OwnerId":"Projects-1","Variables":[]}"""));
            }

            for (var index = 1; index <= 14; index++)
            {
                var id = $"deploymentprocess-Projects-1-s-{index}-redacted";
                documents.Add(Document(
                    id,
                    "DeploymentProcess",
                    $"{id}.json",
                    $$"""{"Id":"{{id}}","OwnerId":"Projects-1","Steps":[]}"""));
            }

            for (var index = 1; index <= 348; index++)
            {
                var id = $"Releases-{index}";
                documents.Add(Document(
                    id,
                    "Release",
                    $"{id}.json",
                    $$"""{"Id":"{{id}}","ProjectId":"Projects-1","Version":"1.0.{{index}}"}"""));
            }

            for (var index = 1; index <= 502; index++)
            {
                var deploymentId = $"Deployments-{index}";
                documents.Add(Document(
                    deploymentId,
                    "Deployment",
                    $"{deploymentId}.json",
                    $$"""{"Id":"{{deploymentId}}","ProjectId":"Projects-1","EnvironmentId":"Environments-1","ReleaseId":"Releases-{{index}}","TaskId":"ServerTasks-{{index}}"}"""));

                var taskId = $"ServerTasks-{index}";
                documents.Add(Document(
                    taskId,
                    "ServerTask",
                    $"{taskId}.json",
                    $$"""{"Id":"{{taskId}}","ProjectId":"Projects-1","EnvironmentId":"Environments-1","State":"Success"}"""));
            }

            var manifest = new
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
            };

            return
            [
                new OctopusExtractedArchiveEntry(
                    "manifest.json",
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest))),
                .. documents.Select(document => new OctopusExtractedArchiveEntry(
                    document.DocumentSource,
                    Encoding.UTF8.GetBytes(document.Json)))
            ];
        }

        private static OctopusVariableSetDto BuildCurrentVariableSet()
        {
            var variables = new List<OctopusVariableDto>
            {
                new()
                {
                    Id = "Variables-K8SNamespace",
                    Name = "K8SNamespace",
                    Value = "next-chat",
                    Scope = { ["Environment"] = ["Environments-1"] }
                },
                new()
                {
                    Id = "Variables-ApiKey",
                    Name = "ApiKey",
                    Type = "Sensitive",
                    IsSensitive = true,
                    Value = VariableSecret,
                    Scope = { ["Environment"] = ["Environments-1"] }
                }
            };

            for (var index = 1; index <= 42; index++)
            {
                variables.Add(new OctopusVariableDto
                {
                    Id = $"Variables-Config{index:00}",
                    Name = $"Config{index:00}",
                    Value = $"value-{index:00}"
                });
            }

            return new OctopusVariableSetDto
            {
                Id = "variableset-Projects-1",
                OwnerId = "Projects-1",
                OwnerType = "Project",
                Variables = variables
            };
        }

        private static OctopusDeploymentProcessDto BuildCurrentProcess()
            => new()
            {
                Id = "deploymentprocess-Projects-1",
                OwnerId = "Projects-1",
                Steps =
                [
                    new OctopusDeploymentStepDto
                    {
                        Id = "Steps-Approval",
                        Name = "Approval-Solar",
                        Actions =
                        [
                            new OctopusDeploymentActionDto
                            {
                                Id = "Actions-Approval",
                                Name = "Approval",
                                ActionType = "Octopus.Manual",
                                IsRequired = true,
                                Properties =
                                {
                                    ["Octopus.Action.Manual.Instructions"] = "Approve the release.",
                                    ["Octopus.Action.Manual.ResponsibleTeamIds"] = "Teams-1"
                                }
                            }
                        ]
                    },
                    new OctopusDeploymentStepDto
                    {
                        Id = "Steps-Containers",
                        Name = "Deploy Kubernetes resources",
                        Actions =
                        [
                            new OctopusDeploymentActionDto
                            {
                                Id = "Actions-Containers",
                                Name = "Deploy containers",
                                ActionType = "Octopus.KubernetesDeployContainers",
                                IsRequired = true,
                                Packages =
                                [
                                    new OctopusActionPackageDto
                                    {
                                        Id = "Packages-Web",
                                        Name = "web",
                                        PackageId = "next-chat/web",
                                        FeedId = "Feeds-1"
                                    }
                                ],
                                Properties =
                                {
                                    ["Octopus.Action.TargetRoles"] = "aws-eks-us",
                                    ["Octopus.Action.KubernetesContainers.Namespace"] = "#{K8SNamespace}",
                                    ["Octopus.Action.KubernetesContainers.Containers"] = """[{"Name":"web","FeedId":"Feeds-1"}]"""
                                }
                            }
                        ]
                    },
                    new OctopusDeploymentStepDto
                    {
                        Id = "Steps-Ingress",
                        Name = "Deploy Kubernetes Ingress",
                        Actions =
                        [
                            new OctopusDeploymentActionDto
                            {
                                Id = "Actions-Ingress",
                                Name = "Deploy ingress",
                                ActionType = "Octopus.KubernetesDeployIngress",
                                IsRequired = true,
                                Properties =
                                {
                                    ["Octopus.Action.KubernetesContainers.IngressName"] = "next-chat",
                                    ["Octopus.Action.KubernetesContainers.IngressRules"] = """[{"host":"next-chat.example","http":{"paths":[{"key":"/","value":"80","option":"web","option2":"Prefix"}]}}]"""
                                }
                            }
                        ]
                    }
                ]
            };

        private static FixtureDocument Document(
            string id,
            string documentType,
            string documentSource,
            string json)
            => new(id, documentType, documentSource, json);

        private static string Sha1(string json)
            => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

        private sealed record FixtureDocument(
            string Id,
            string DocumentType,
            string DocumentSource,
            string Json);
    }
}
