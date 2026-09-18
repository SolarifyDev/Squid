using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Mapping.Actions;

public class OctopusManualActionMapperTests
{
    private readonly OctopusManualActionMapper _mapper = new();

    [Fact]
    public void Map_MapsInstructionsAndResponsibleTeams()
    {
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-Manual",
            Name = "Approval",
            ActionType = "Octopus.Manual",
            Properties =
            {
                ["Octopus.Action.Manual.Instructions"] = "Check #{Environment.Name}",
                ["Octopus.Action.Manual.ResponsibleTeamIds"] = "Teams-1, Teams-2"
            }
        };

        var result = _mapper.Map(action, Context(includeSecondTeam: true));

        result.HasBlockers.ShouldBeFalse();
        result.Action.ActionType.ShouldBe(SpecialVariables.ActionTypes.Manual);
        result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.ManualInstructions).PropertyValue.ShouldBe("Check #{Environment.Name}");
        result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.ManualResponsibleTeamIds).PropertyValue.ShouldBe("401,402");
    }

    [Fact]
    public void Map_WhenResponsibleTeamMappingIsMissing_AddsBlockerAndKeepsResolvedTeams()
    {
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-Manual",
            Name = "Approval",
            ActionType = "Octopus.Manual",
            Properties =
            {
                ["Octopus.Action.Manual.ResponsibleTeamIds"] = "Teams-1, Teams-Missing"
            }
        };

        var result = _mapper.Map(action, Context(includeSecondTeam: false));

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportActionMappingDiagnosticCodes.MissingResponsibleTeamMapping);
        result.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.ManualResponsibleTeamIds).PropertyValue.ShouldBe("401");
    }

    private static OctopusImportActionMappingContext Context(bool includeSecondTeam)
    {
        var idMap = new OctopusImportIdMap();
        idMap.AddReused(Resource("Teams-1", "Release approvers"), 401);

        if (includeSecondTeam)
            idMap.AddReused(Resource("Teams-2", "SRE"), 402);

        return new OctopusImportActionMappingContext(idMap, 7);
    }

    private static OctopusResourceNode Resource(string sourceId, string name)
        => new(
            sourceId,
            name,
            OctopusResourceKind.Team,
            OctopusDocumentKind.Team,
            $"{sourceId}.json",
            null,
            null,
            false,
            new OctopusTeamDto());
}
