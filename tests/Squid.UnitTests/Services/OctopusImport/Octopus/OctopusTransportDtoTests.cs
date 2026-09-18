using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport.Octopus;

public class OctopusTransportDtoTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void ManifestDto_DeserializesEntriesAndPreservesUnknownFields()
    {
        const string json = """
        {
          "SchemaVersions": [ "Script0271InitialiseGuidedFailureEnumValue" ],
          "Entries": [
            {
              "Id": "Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43",
              "Name": "Next Chat",
              "DocumentType": "Project",
              "ExportType": "FullDocument",
              "DocumentSource": "Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43.json",
              "ParentId": null,
              "Hash": "cc80017a933e6da5e3f7d55d574d11e10c9877cf",
              "FutureField": "kept"
            }
          ],
          "OtherManifestField": true
        }
        """;

        var manifest = JsonSerializer.Deserialize<OctopusExportManifestDto>(json, JsonOptions);

        manifest.SchemaVersions.ShouldBe(["Script0271InitialiseGuidedFailureEnumValue"]);
        manifest.Entries.Count.ShouldBe(1);
        manifest.Entries[0].DocumentType.ShouldBe("Project");
        manifest.Entries[0].ExtensionData.Keys.ShouldContain("FutureField");
        manifest.ExtensionData.Keys.ShouldContain("OtherManifestField");
    }

    [Fact]
    public void ProjectDto_DeserializesCoreProjectReferences()
    {
        const string json = """
        {
          "SpaceId": "Spaces-1-D4C63A85E7894C5D8C20D9297FEA1A43",
          "Id": "Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43",
          "Name": "Next Chat",
          "Slug": "next-chat",
          "Description": "",
          "IsDisabled": false,
          "VariableSetId": "variableset-Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43",
          "DeploymentProcessId": "deploymentprocess-Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43",
          "DeploymentSettingsId": "deploymentsettings-Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43",
          "ProjectGroupId": "ProjectGroups-210-D4C63A85E7894C5D8C20D9297FEA1A43",
          "LifecycleId": "Lifecycles-302-D4C63A85E7894C5D8C20D9297FEA1A43",
          "IncludedLibraryVariableSetIds": [],
          "ExtensionSettings": [],
          "DataVersion": "AAAAAAIhdD8="
        }
        """;

        var project = JsonSerializer.Deserialize<OctopusProjectDto>(json, JsonOptions);

        project.Id.ShouldBe("Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43");
        project.Name.ShouldBe("Next Chat");
        project.VariableSetId.ShouldStartWith("variableset-Projects-1323");
        project.DeploymentProcessId.ShouldStartWith("deploymentprocess-Projects-1323");
        project.ProjectGroupId.ShouldStartWith("ProjectGroups-210");
        project.LifecycleId.ShouldStartWith("Lifecycles-302");
    }

    [Fact]
    public void DeploymentProcessDto_DeserializesStepsActionsPackagesAndProperties()
    {
        const string json = """
        {
          "Id": "deploymentprocess-Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43",
          "OwnerId": "Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43",
          "Version": 21,
          "SpaceId": "Spaces-1-D4C63A85E7894C5D8C20D9297FEA1A43",
          "Steps": [
            {
              "Id": "13149498-f8e1-436f-9110-288719b4dd80",
              "Name": "Deploy Kubernetes resources",
              "Condition": "Success",
              "StartTrigger": "StartAfterPrevious",
              "PackageRequirement": "LetOctopusDecide",
              "Actions": [
                {
                  "Id": "b3823849-4b09-4ace-871b-fee35641a13c",
                  "Name": "Deploy Kubernetes resources",
                  "ActionType": "Octopus.KubernetesDeployContainers",
                  "IsDisabled": false,
                  "IsRequired": false,
                  "Environments": [],
                  "ExcludedEnvironments": [],
                  "Channels": [],
                  "TenantTags": [],
                  "Packages": [
                    {
                      "Id": "95caadda-feaa-44aa-b67b-66646fc78336",
                      "Name": "next-chat",
                      "PackageId": "sjdistributor/next-chat",
                      "FeedId": "Feeds-1083-D4C63A85E7894C5D8C20D9297FEA1A43",
                      "AcquisitionLocation": "NotAcquired",
                      "Properties": { "SelectionMode": "immediate" }
                    }
                  ],
                  "GitDependencies": [],
                  "Condition": "Success",
                  "Properties": {
                    "Octopus.Action.KubernetesContainers.Namespace": "#{K8SNameSpace}"
                  }
                }
              ],
              "Properties": {}
            }
          ]
        }
        """;

        var process = JsonSerializer.Deserialize<OctopusDeploymentProcessDto>(json, JsonOptions);

        process.Version.ShouldBe(21);
        process.Steps.Count.ShouldBe(1);
        process.Steps[0].Actions[0].ActionType.ShouldBe("Octopus.KubernetesDeployContainers");
        process.Steps[0].Actions[0].Packages[0].FeedId.ShouldStartWith("Feeds-1083");
        process.Steps[0].Actions[0].Properties["Octopus.Action.KubernetesContainers.Namespace"].ShouldBe("#{K8SNameSpace}");
    }

    [Fact]
    public void VariableSetDto_DeserializesVariableScopes()
    {
        const string json = """
        {
          "Id": "variableset-Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43",
          "OwnerId": "Projects-1323-D4C63A85E7894C5D8C20D9297FEA1A43",
          "OwnerType": "Project",
          "Version": 55,
          "Variables": [
            {
              "Id": "dc3df21e-8a46-737c-7d98-4abae6378e2f",
              "Name": "K8SNameSpace",
              "Value": "next-chat-prd",
              "Scope": {
                "Environment": [ "Environments-3-D4C63A85E7894C5D8C20D9297FEA1A43" ]
              }
            }
          ]
        }
        """;

        var variableSet = JsonSerializer.Deserialize<OctopusVariableSetDto>(json, JsonOptions);

        variableSet.OwnerType.ShouldBe("Project");
        variableSet.Version.ShouldBe(55);
        variableSet.Variables[0].Name.ShouldBe("K8SNameSpace");
        variableSet.Variables[0].Scope["Environment"].ShouldBe(["Environments-3-D4C63A85E7894C5D8C20D9297FEA1A43"]);
    }

    [Fact]
    public void FeedDto_DeserializesCredentialsForLaterRedaction()
    {
        const string json = """
        {
          "FeedType": "Docker",
          "Username": "sjdistributor",
          "Password": "encrypted-value",
          "FeedUri": "https://index.docker.io",
          "Id": "Feeds-1083-D4C63A85E7894C5D8C20D9297FEA1A43",
          "Name": "Docker Hub Registry",
          "SpaceId": "Spaces-1-D4C63A85E7894C5D8C20D9297FEA1A43"
        }
        """;

        var feed = JsonSerializer.Deserialize<OctopusFeedDto>(json, JsonOptions);

        feed.FeedType.ShouldBe("Docker");
        feed.Username.ShouldBe("sjdistributor");
        feed.Password.ShouldBe("encrypted-value");
        feed.FeedUri.ShouldBe("https://index.docker.io");
    }
}
