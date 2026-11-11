using System.Linq;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Mapping;

public class OctopusReferenceListTests
{
    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData(",;\n\r", new string[0])]
    [InlineData(" , ; \r\n ", new string[0])]
    [InlineData("Teams-1", new[] { "Teams-1" })]
    [InlineData("Teams-1,Teams-2", new[] { "Teams-1", "Teams-2" })]
    [InlineData("Teams-1;Teams-2", new[] { "Teams-1", "Teams-2" })]
    [InlineData("Teams-1\nTeams-2", new[] { "Teams-1", "Teams-2" })]
    [InlineData("Teams-1\r\nTeams-2", new[] { "Teams-1", "Teams-2" })]
    [InlineData("Teams-1\rTeams-2", new[] { "Teams-1", "Teams-2" })]
    [InlineData("  Teams-1 ,\t Teams-2 ;; \r\n Teams-3 ", new[] { "Teams-1", "Teams-2", "Teams-3" })]
    [InlineData("web server,api", new[] { "web server", "api" })]
    [InlineData("Teams-1,Teams-1", new[] { "Teams-1", "Teams-1" })]
    public void Split_HandlesEverySeparatorWhitespaceAndEmptyEntry(string value, string[] expected)
    {
        OctopusReferenceList.Split(value).ShouldBe(expected);
    }

    // Seam: the graph builder (which drives the preview blockers) and the confirmation mappers must read
    // the SAME list out of the same property value — otherwise preview blocks on a team the mapper would
    // have mapped (or, worse, passes a team the mapper silently drops).
    [Theory]
    [InlineData("Teams-1,Teams-2")]
    [InlineData("Teams-1;Teams-2")]
    [InlineData("Teams-1\nTeams-2")]
    [InlineData("Teams-1\r\nTeams-2")]
    [InlineData(" Teams-1 ;\r\n, Teams-2 ,")]
    public async Task GraphBuilderAndManualMapper_ReadTheSameResponsibleTeams(string responsibleTeamIds)
    {
        var scenario = new OctopusImportScenario()
            .Team("Teams-1", "Solar")
            .Team("Teams-2", "Lunar")
            .Project("Projects-1", "Api", [], [], OctopusImportScenario.ManualAction("Actions-1", responsibleTeamIds));

        var result = await scenario.PlanAsync();

        var graphTeams = result.Graph.References.Where(r => r.FromSourceId == "Actions-1" && r.ReferenceKind == OctopusResourceReferenceKind.Team).Select(r => r.ToSourceId).ToList();
        var mapped = MapManualAction(result.Graph, ("Teams-1", 501), ("Teams-2", 502));

        graphTeams.ShouldBe(["Teams-1", "Teams-2"]);
        mapped.Diagnostics.ShouldBeEmpty();
        mapped.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.ManualResponsibleTeamIds).PropertyValue.ShouldBe("501,502");
        result.Blockers.Select(b => b.Code).ShouldNotContain(Squid.Core.Services.OctopusImport.OctopusImportPreviewDiagnosticCodes.MissingResponsibleTeam);
    }

    [Theory]
    [InlineData("web\napi")]
    [InlineData("web;api")]
    [InlineData("web\r\napi")]
    public async Task GraphBuilder_SplitsActionTargetRolesLikeTheProcessMapper(string targetRoles)
    {
        var scenario = new OctopusImportScenario()
            .Machine("Machines-1", "web-1", "web", "api")
            .Project("Projects-1", "Api", [], [], OctopusImportScenario.ScriptAction("Actions-1", "echo hi", new Dictionary<string, string> { ["Octopus.Action.TargetRoles"] = targetRoles }));

        var result = await scenario.PlanAsync();

        result.Graph.References.Where(r => r.FromSourceId == "Actions-1" && r.ReferenceKind == OctopusResourceReferenceKind.TargetRole).Select(r => r.ToSourceId).ShouldBe(["web", "api"]);
        result.Blockers.Select(b => b.Code).ShouldNotContain(Squid.Core.Services.OctopusImport.OctopusImportPreviewDiagnosticCodes.MissingTargetRole);
    }

    private static Squid.Core.Services.OctopusImport.Mapping.Actions.OctopusImportActionMappingResult MapManualAction(OctopusResourceGraph graph, params (string SourceId, int DestinationId)[] teams)
    {
        var idMap = new Squid.Core.Services.OctopusImport.OctopusImportIdMap();

        foreach (var team in teams)
            idMap.AddCreated(graph.Resources.Single(r => r.SourceId == team.SourceId), team.DestinationId);

        var action = graph.Resources.Single(r => r.SourceId == "Actions-1").GetSource<OctopusDeploymentActionDto>();

        return new OctopusManualActionMapper().Map(action, new OctopusImportActionMappingContext(idMap, 7));
    }
}
