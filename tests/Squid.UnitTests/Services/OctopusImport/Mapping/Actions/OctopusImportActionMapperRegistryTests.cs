using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.OctopusImport.Mapping.Actions;

public class OctopusImportActionMapperRegistryTests
{
    [Fact]
    public void Map_ReturnsUnsupportedDiagnostic_WhenMapperIsMissing()
    {
        var registry = new OctopusImportActionMapperRegistry([]);
        var action = new OctopusDeploymentActionDto
        {
            Id = "a-1",
            Name = "Deploy app",
            ActionType = "Octopus.Unknown"
        };
        var context = new OctopusImportActionMappingContext(new OctopusImportIdMap(), 42);

        var result = registry.Map(action, context);

        result.Action.ShouldBeNull();
        result.HasBlockers.ShouldBeFalse();
        result.Diagnostics.ShouldContain(d => d.Code == OctopusImportActionMappingDiagnosticCodes.UnsupportedActionType);
        result.Diagnostics.ShouldContain(d => d.Code == OctopusImportActionMappingDiagnosticCodes.UnsupportedActionSkipped);
        result.Diagnostics.All(d => d.Severity == OctopusImportCompatibilitySeverity.Warning).ShouldBeTrue();
    }

    [Fact]
    public void Map_CreatesRedactedDisabledPlaceholder_WhenConfiguredForDisabledPlaceholder()
    {
        const string secretLikeName = "Rotate password secret";
        var registry = new OctopusImportActionMapperRegistry([]);
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-123",
            Name = secretLikeName,
            Slug = "deploy-worker",
            ActionType = "Octopus.CustomCommunityStep",
            Properties =
            {
                ["Octopus.Action.Custom.Password"] = "must-not-be-copied"
            }
        };
        var context = new OctopusImportActionMappingContext(
            new OctopusImportIdMap(),
            42,
            OctopusImportUnsupportedActionHandling.DisabledPlaceholder);

        var result = registry.Map(action, context);

        result.HasBlockers.ShouldBeFalse();
        result.Action.ShouldNotBeNull();
        result.Action.Name.ShouldBe(OctopusImportActionMapperRegistry.RedactedValue);
        result.Action.ActionType.ShouldBe(SpecialVariables.ActionTypes.Script);
        result.Action.IsDisabled.ShouldBeTrue();
        result.Action.Properties.Single(p => p.PropertyName == OctopusImportActionMapperRegistry.PlaceholderSourceIdProperty).PropertyValue.ShouldBe("Actions-123");
        result.Action.Properties.Single(p => p.PropertyName == OctopusImportActionMapperRegistry.PlaceholderSourceNameProperty).PropertyValue.ShouldBe(OctopusImportActionMapperRegistry.RedactedValue);
        result.Action.Properties.Single(p => p.PropertyName == OctopusImportActionMapperRegistry.PlaceholderSourceSlugProperty).PropertyValue.ShouldBe("deploy-worker");
        result.Action.Properties.Single(p => p.PropertyName == OctopusImportActionMapperRegistry.PlaceholderSourceActionTypeProperty).PropertyValue.ShouldBe("Octopus.CustomCommunityStep");
        result.Action.Properties.All(p => p.PropertyValue.Contains("must-not-be-copied", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
        result.Diagnostics.All(d => d.Message.Contains(secretLikeName, StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportActionMappingDiagnosticCodes.UnsupportedActionPlaceholderCreated);
    }

    [Fact]
    public void Map_DelegatesToRegisteredMapper_CaseInsensitively()
    {
        var mapper = new RecordingMapper();
        var registry = new OctopusImportActionMapperRegistry([mapper]);
        var action = new OctopusDeploymentActionDto
        {
            Id = "a-2",
            Name = "Manual step",
            ActionType = "octopus.manual"
        };
        var context = new OctopusImportActionMappingContext(new OctopusImportIdMap(), 7);

        var result = registry.Map(action, context);

        mapper.Invocations.ShouldBe(1);
        mapper.LastAction.ShouldBe(action);
        mapper.LastContext.ShouldBe(context);
        result.Action.ActionType.ShouldBe(SpecialVariables.ActionTypes.Manual);
        result.Action.Name.ShouldBe("Manual step");
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void SupportedActionTypes_ExposesRegisteredActionTypes()
    {
        var registry = new OctopusImportActionMapperRegistry([new RecordingMapper()]);

        registry.SupportedActionTypes.ShouldContain("Octopus.Manual");
    }

    [Fact]
    public void Constructor_Throws_WhenDuplicateActionTypeIsRegistered()
    {
        var mapper1 = new RecordingMapper("Octopus.Manual", SpecialVariables.ActionTypes.Manual);
        var mapper2 = new RecordingMapper("octopus.manual", SpecialVariables.ActionTypes.Manual);

        Should.Throw<InvalidOperationException>(() => new OctopusImportActionMapperRegistry([mapper1, mapper2]))
            .Message.ShouldContain(OctopusImportActionMappingDiagnosticCodes.DuplicateActionMapperRegistration);
    }

    private sealed class RecordingMapper : IOctopusImportActionMapper
    {
        public RecordingMapper()
            : this("Octopus.Manual", SpecialVariables.ActionTypes.Manual)
        {
        }

        public RecordingMapper(string octopusActionType, string squidActionType)
        {
            OctopusActionType = octopusActionType;
            SquidActionType = squidActionType;
        }

        public int Invocations { get; private set; }

        public OctopusDeploymentActionDto LastAction { get; private set; }

        public OctopusImportActionMappingContext LastContext { get; private set; }

        public string OctopusActionType { get; }

        public string SquidActionType { get; }

        public OctopusImportActionMappingResult Map(
            OctopusDeploymentActionDto action,
            OctopusImportActionMappingContext context)
        {
            Invocations++;
            LastAction = action;
            LastContext = context;

            return new OctopusImportActionMappingResult(
                new CreateOrUpdateDeploymentActionModel
                {
                    Name = action.Name,
                    ActionType = SquidActionType
                },
                []);
        }
    }
}
