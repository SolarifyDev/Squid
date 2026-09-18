using System.Linq;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Mapping;

public class OctopusImportProjectMapperTests
{
    private readonly OctopusImportProjectMapper _mapper = new();

    [Fact]
    public void MapToCreateOrUpdateModel_MapsProjectFieldsAndDestinationReferences()
    {
        var project = Project();
        var idMap = new OctopusImportIdMap();
        idMap.AddReused(Resource("ProjectGroups-1", OctopusResourceKind.ProjectGroup, "Default", new OctopusProjectGroupDto()), 10);
        idMap.AddReused(Resource("Lifecycles-1", OctopusResourceKind.Lifecycle, "Default", new OctopusLifecycleDto()), 20);

        var result = _mapper.MapToCreateOrUpdateModel(
            Resource(project.Id, OctopusResourceKind.Project, project.Name, project),
            idMap,
            7);

        result.HasBlockers.ShouldBeFalse();
        result.Project.Name.ShouldBe("Octopus Project");
        result.Project.Slug.ShouldBe("octopus-project");
        result.Project.IsDisabled.ShouldBeTrue();
        result.Project.AutoCreateRelease.ShouldBeTrue();
        result.Project.DiscreteChannelRelease.ShouldBeTrue();
        result.Project.ProjectGroupId.ShouldBe(10);
        result.Project.LifecycleId.ShouldBe(20);
        result.Project.SpaceId.ShouldBe(7);
        result.Project.IncludedLibraryVariableSetIds.ShouldBeEmpty();
    }

    [Fact]
    public void MapToCreateOrUpdateModel_WhenRequiredReferencesAreMissing_AddsBlockers()
    {
        var project = Project();

        var result = _mapper.MapToCreateOrUpdateModel(
            Resource(project.Id, OctopusResourceKind.Project, project.Name, project),
            new OctopusImportIdMap(),
            7);

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Count(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).ShouldBe(2);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportProjectMappingDiagnosticCodes.MissingProjectGroupMapping);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportProjectMappingDiagnosticCodes.MissingLifecycleMapping);
    }

    [Fact]
    public void MapToCreateOrUpdateModel_PreservesDeploymentSettingsAndDefaultChannelAsMetadata()
    {
        var project = Project();
        var idMap = new OctopusImportIdMap();
        idMap.AddReused(Resource("ProjectGroups-1", OctopusResourceKind.ProjectGroup, "Default", new OctopusProjectGroupDto()), 10);
        idMap.AddReused(Resource("Lifecycles-1", OctopusResourceKind.Lifecycle, "Default", new OctopusLifecycleDto()), 20);
        var deploymentSettings = new OctopusDeploymentSettingsDto
        {
            Id = "deploymentsettings-Projects-1",
            DefaultGuidedFailureMode = "EnvironmentDefault",
            DefaultToSkipIfAlreadyInstalled = true,
            ForcePackageDownload = true,
            FailTargetDiscovery = true
        };
        var defaultChannel = new OctopusChannelDto
        {
            Id = "Channels-1",
            Name = "Default",
            LifecycleId = "Lifecycles-1",
            Rules = [JsonDocument.Parse("""{"tag":"latest"}""").RootElement.Clone()]
        };

        var result = _mapper.MapToCreateOrUpdateModel(
            Resource(project.Id, OctopusResourceKind.Project, project.Name, project),
            idMap,
            7,
            deploymentSettings,
            defaultChannel);

        using var metadata = JsonDocument.Parse(result.Project.Json);
        metadata.RootElement.GetProperty("sourceId").GetString().ShouldBe("Projects-1");
        metadata.RootElement.GetProperty("deploymentSettings").GetProperty("sourceId").GetString().ShouldBe("deploymentsettings-Projects-1");
        metadata.RootElement.GetProperty("deploymentSettings").GetProperty("forcePackageDownload").GetBoolean().ShouldBeTrue();
        metadata.RootElement.GetProperty("defaultChannel").GetProperty("sourceId").GetString().ShouldBe("Channels-1");
        metadata.RootElement.GetProperty("defaultChannel").GetProperty("ruleCount").GetInt32().ShouldBe(1);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportProjectMappingDiagnosticCodes.DeploymentSettingsStoredAsMetadata);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportProjectMappingDiagnosticCodes.ChannelDefaultsStoredAsMetadata);
    }

    [Fact]
    public void MapToCreateOrUpdateModel_WhenDeferredProjectFeaturesExist_AddsWarnings()
    {
        var project = Project();
        project.IncludedLibraryVariableSetIds.Add("LibraryVariableSets-1");
        project.TenantedDeploymentMode = "TenantedOrUntenanted";
        var idMap = new OctopusImportIdMap();
        idMap.AddReused(Resource("ProjectGroups-1", OctopusResourceKind.ProjectGroup, "Default", new OctopusProjectGroupDto()), 10);
        idMap.AddReused(Resource("Lifecycles-1", OctopusResourceKind.Lifecycle, "Default", new OctopusLifecycleDto()), 20);

        var result = _mapper.MapToCreateOrUpdateModel(
            Resource(project.Id, OctopusResourceKind.Project, project.Name, project),
            idMap,
            7);

        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportProjectMappingDiagnosticCodes.IncludedLibraryVariableSetsDeferred);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportProjectMappingDiagnosticCodes.UnsupportedTenantedDeploymentMode);
        result.Diagnostics.All(d => d.Severity == OctopusImportCompatibilitySeverity.Warning).ShouldBeTrue();
    }

    [Fact]
    public void MapToCreateOrUpdateModel_WhenResourceIsNotProject_Throws()
    {
        Should.Throw<ArgumentException>(() => _mapper.MapToCreateOrUpdateModel(
            Resource("Feeds-1", OctopusResourceKind.Feed, "Feed", new OctopusFeedDto()),
            new OctopusImportIdMap(),
            7));
    }

    private static OctopusProjectDto Project()
        => new()
        {
            Id = "Projects-1",
            Name = "Octopus Project",
            Slug = "octopus-project",
            Description = "Imported from Octopus.",
            IsDisabled = true,
            ProjectGroupId = "ProjectGroups-1",
            LifecycleId = "Lifecycles-1",
            VariableSetId = "variableset-Projects-1",
            DeploymentProcessId = "deploymentprocess-Projects-1",
            DeploymentSettingsId = "deploymentsettings-Projects-1",
            AutoCreateRelease = true,
            DiscreteChannelRelease = true
        };

    private static OctopusResourceNode Resource(
        string sourceId,
        OctopusResourceKind kind,
        string name,
        object source)
        => new(
            sourceId,
            name,
            kind,
            OctopusDocumentKind.Project,
            $"{sourceId}.json",
            sourceId.StartsWith("Projects-", StringComparison.OrdinalIgnoreCase) ? sourceId : null,
            null,
            false,
            source);
}
