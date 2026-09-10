using System.Linq;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.Deployments;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Mapping;

public class OctopusImportEnvironmentLifecycleMapperTests
{
    private readonly OctopusImportEnvironmentMapper _environmentMapper = new();
    private readonly OctopusImportLifecycleMapper _lifecycleMapper = new();

    [Fact]
    public void MapToCreateModel_MapsEnvironmentFieldsIntoCreateCommand()
    {
        var environment = new OctopusEnvironmentDto
        {
            Id = "Environments-1",
            Name = "Production",
            Slug = "production",
            Description = "Live traffic",
            SortOrder = 5,
            UseGuidedFailure = true,
            AllowDynamicInfrastructure = true
        };

        var result = _environmentMapper.MapToCreateModel(
            Resource(environment.Id, OctopusResourceKind.Environment, environment.Name, environment),
            7);

        result.HasBlockers.ShouldBeFalse();
        result.Environment.SpaceId.ShouldBe(7);
        result.Environment.Name.ShouldBe("Production");
        result.Environment.Slug.ShouldBe("production");
        result.Environment.Description.ShouldBe("Live traffic");
        result.Environment.SortOrder.ShouldBe(5);
        result.Environment.UseGuidedFailure.ShouldBeTrue();
        result.Environment.AllowDynamicInfrastructure.ShouldBeTrue();
    }

    [Fact]
    public void MapToCreateOrUpdateModel_MapsLifecyclePhasesAndRemapsEnvironmentIds()
    {
        var lifecycle = Lifecycle();
        var idMap = new OctopusImportIdMap();
        idMap.AddReused(Resource("Environments-1", OctopusResourceKind.Environment, "Production", new OctopusEnvironmentDto()), 101);
        idMap.AddReused(Resource("Environments-2", OctopusResourceKind.Environment, "Staging", new OctopusEnvironmentDto()), 102);

        var result = _lifecycleMapper.MapToCreateOrUpdateModel(
            Resource(lifecycle.Id, OctopusResourceKind.Lifecycle, lifecycle.Name, lifecycle),
            idMap,
            9);

        result.HasBlockers.ShouldBeFalse();
        result.Lifecycle.Lifecycle.Name.ShouldBe("Default");
        result.Lifecycle.Lifecycle.Slug.ShouldBe("default");
        result.Lifecycle.Lifecycle.SpaceId.ShouldBe(9);
        result.Lifecycle.Lifecycle.ReleaseRetentionKeepForever.ShouldBeFalse();
        result.Lifecycle.Lifecycle.ReleaseRetentionQuantity.ShouldBe(30);
        result.Lifecycle.Lifecycle.ReleaseRetentionUnit.ShouldBe(RetentionPolicyUnit.Days);
        result.Lifecycle.Lifecycle.TentacleRetentionKeepForever.ShouldBeTrue();
        result.Lifecycle.Phases.Count.ShouldBe(2);
        result.Lifecycle.Phases[0].SortOrder.ShouldBe(0);
        result.Lifecycle.Phases[0].AutomaticDeploymentTargetIds.ShouldBe([101]);
        result.Lifecycle.Phases[0].OptionalDeploymentTargetIds.ShouldBe([102]);
        result.Lifecycle.Phases[0].ReleaseRetentionKeepForever.ShouldBe(false);
        result.Lifecycle.Phases[0].ReleaseRetentionQuantity.ShouldBe(7);
        result.Lifecycle.Phases[0].ReleaseRetentionUnit.ShouldBe(RetentionPolicyUnit.Weeks);
        result.Lifecycle.Phases[1].SortOrder.ShouldBe(1);
        result.Lifecycle.Phases[1].AutomaticDeploymentTargetIds.ShouldBe([102]);
        result.Lifecycle.Phases[1].OptionalDeploymentTargetIds.ShouldBeEmpty();
    }

    [Fact]
    public void MapToCreateOrUpdateModel_WhenEnvironmentMappingIsMissing_AddsBlockerAndDropsMissingTarget()
    {
        var lifecycle = Lifecycle();
        lifecycle.Phases[0].AutomaticDeploymentTargets.Add("Environments-999");

        var result = _lifecycleMapper.MapToCreateOrUpdateModel(
            Resource(lifecycle.Id, OctopusResourceKind.Lifecycle, lifecycle.Name, lifecycle),
            new OctopusImportIdMap(),
            9);

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportLifecycleMappingDiagnosticCodes.MissingAutomaticDeploymentEnvironmentMapping);
        result.Lifecycle.Phases[0].AutomaticDeploymentTargetIds.ShouldBeEmpty();
    }

    [Fact]
    public void MapToCreateOrUpdateModel_WhenRetentionPolicyShapeIsUnsupported_AddsWarningAndUsesSafeDefaults()
    {
        var lifecycle = Lifecycle();
        lifecycle.ReleaseRetentionPolicy = JsonDocument.Parse("""{"Unsupported":"value"}""").RootElement.Clone();
        lifecycle.Phases[0].ReleaseRetentionPolicy = JsonDocument.Parse("""{"Unsupported":"value"}""").RootElement.Clone();

        var result = _lifecycleMapper.MapToCreateOrUpdateModel(
            Resource(lifecycle.Id, OctopusResourceKind.Lifecycle, lifecycle.Name, lifecycle),
            new OctopusImportIdMap(),
            9);

        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportLifecycleMappingDiagnosticCodes.UnsupportedLifecycleRetentionPolicy);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportLifecycleMappingDiagnosticCodes.UnsupportedPhaseRetentionPolicy);
        result.Lifecycle.Lifecycle.ReleaseRetentionKeepForever.ShouldBeTrue();
        result.Lifecycle.Phases[0].ReleaseRetentionUnit.ShouldBe(RetentionPolicyUnit.Items);
        result.Lifecycle.Phases[0].ReleaseRetentionQuantity.ShouldBe(0);
        result.Lifecycle.Phases[0].ReleaseRetentionKeepForever.ShouldBe(true);
    }

    [Fact]
    public void MapToCreateOrUpdateModel_WhenResourceKindIsWrong_Throws()
    {
        Should.Throw<ArgumentException>(() => _lifecycleMapper.MapToCreateOrUpdateModel(
            Resource("Environments-1", OctopusResourceKind.Environment, "Production", new OctopusEnvironmentDto()),
            new OctopusImportIdMap(),
            9));
    }

    private static OctopusLifecycleDto Lifecycle()
        => new()
        {
            Id = "Lifecycles-1",
            Name = "Default",
            Slug = "default",
            ReleaseRetentionPolicy = JsonDocument.Parse("""{"ShouldKeepForever":false,"QuantityToKeep":30,"Unit":"Days"}""").RootElement.Clone(),
            TentacleRetentionPolicy = JsonDocument.Parse("""{"ShouldKeepForever":true,"QuantityToKeep":0,"Unit":"Items"}""").RootElement.Clone(),
            Phases =
            [
                new OctopusLifecyclePhaseDto
                {
                    Id = "Phase-1",
                    Name = "Deploy",
                    MinimumEnvironmentsBeforePromotion = 1,
                    AutomaticDeploymentTargets = ["Environments-1"],
                    OptionalDeploymentTargets = ["Environments-2"],
                    ReleaseRetentionPolicy = JsonDocument.Parse("""{"ShouldKeepForever":false,"QuantityToKeep":7,"Unit":"Weeks"}""").RootElement.Clone()
                },
                new OctopusLifecyclePhaseDto
                {
                    Id = "Phase-2",
                    Name = "Production",
                    MinimumEnvironmentsBeforePromotion = 0,
                    AutomaticDeploymentTargets = ["Environments-2"]
                }
            ]
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
            OctopusDocumentKind.Lifecycle,
            $"{sourceId}.json",
            sourceId.StartsWith("Projects-", StringComparison.OrdinalIgnoreCase) ? sourceId : null,
            null,
            false,
            source);
}
