using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using static Squid.UnitTests.Services.OctopusImport.OctopusImportScenario;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// The reference scope with a project selection: the roots are the chosen projects and what they own, the shared resources they use
/// (directly or through what they use) are in scope, and everything else is outside the selection — except the shared resources Squid
/// never imports (machines, certificates, …), which keep today's report. Without a selection the scope is D4, unchanged.
/// </summary>
public class OctopusImportProjectSelectionScopeTests
{
    // Projects-1 uses Lifecycles-1 (→ Environments-1). Projects-2 uses LibraryVariableSets-1 (a variable scoped to Environments-2),
    // Feeds-1 through a step and Feeds-2 through its release. Feeds-3 and Environments-3 nobody uses; Machines-1 is never imported.
    private static readonly OctopusImportScenario Space = new OctopusImportScenario()
        .ProjectOnLifecycle("Projects-1", "Web", "Lifecycles-1")
        .Lifecycle("Lifecycles-1", "Main", "Environments-1")
        .Environment("Environments-1", "Dev")
        .Environment("Environments-2", "Test")
        .Environment("Environments-3", "Unused env")
        .Project("Projects-2", "Api", ["LibraryVariableSets-1"], [], PackageScriptAction("deploy", "Feeds-1"))
        .Library("LibraryVariableSets-1", "Shared", Var("lib-var", "Url", "x", Scope("Environment", "Environments-2")))
        .Feed("Feeds-1", "Registry", "Docker")
        .Feed("Feeds-2", "Old registry", "Docker")
        .Feed("Feeds-3", "Unused registry", "Docker")
        .Machine("Machines-1", "web-01", "web")
        .Channel("Channels-1", "Projects-2", "Default")
        .ReleaseWithPackage("Releases-1", "Projects-2", "Channels-1", "0.1.0", "deploy", "Feeds-2");

    [Theory]
    [InlineData("Projects-1")]
    [InlineData("deploymentprocess-Projects-1")]
    [InlineData("variableset-Projects-1")]
    [InlineData("Lifecycles-1")]
    [InlineData("Lifecycles-1/phase/Lifecycles-1-phase")]
    [InlineData("Environments-1")]
    public async Task ChooseFirst_ItAndWhatItUses_InScope(string sourceId)
    {
        var scope = await ScopeAsync("Projects-1");

        scope.IsReferenced(sourceId).ShouldBeTrue();
        scope.IsOutsideSelection(await NodeAsync(sourceId)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Projects-2")]
    [InlineData("deploymentprocess-Projects-2")]
    [InlineData("Projects-2/action/deploy")]
    [InlineData("variableset-Projects-2")]
    [InlineData("Channels-1")]
    [InlineData("Releases-1")]
    [InlineData("LibraryVariableSets-1")]
    [InlineData("LibraryVariableSets-1/variable/lib-var")]
    [InlineData("Feeds-1")]
    [InlineData("Feeds-2")]
    [InlineData("Feeds-3")]
    [InlineData("Environments-2")]
    [InlineData("Environments-3")]
    public async Task ChooseFirst_TheOtherProjectAndWhatOnlyItUses_OutsideSelection(string sourceId)
    {
        var scope = await ScopeAsync("Projects-1");

        scope.IsOutsideSelection(await NodeAsync(sourceId)).ShouldBeTrue();
    }

    // Through its library's variable scope, its step's feed and its release's package feed.
    [Theory]
    [InlineData("Projects-2")]
    [InlineData("Channels-1")]
    [InlineData("Releases-1")]
    [InlineData("LibraryVariableSets-1")]
    [InlineData("LibraryVariableSets-1/variable/lib-var")]
    [InlineData("Environments-2")]
    [InlineData("Feeds-1")]
    [InlineData("Feeds-2")]
    public async Task ChooseSecond_SharedResourcesItUses_InScope(string sourceId)
    {
        var scope = await ScopeAsync("Projects-2");

        scope.IsOutsideSelection(await NodeAsync(sourceId)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Projects-1")]
    [InlineData("Lifecycles-1")]
    [InlineData("Environments-1")]
    public async Task ChooseSecond_WhatOnlyTheFirstUses_OutsideSelection(string sourceId)
    {
        var scope = await ScopeAsync("Projects-2");

        scope.IsOutsideSelection(await NodeAsync(sourceId)).ShouldBeTrue();
    }

    // Machines are never imported and no project "uses" one in the graph (they join by role): today's report stays.
    [Fact]
    public async Task SharedKindSquidNeverImports_NotOutsideSelection()
    {
        var scope = await ScopeAsync("Projects-1");

        scope.IsOutsideSelection(await NodeAsync("Machines-1")).ShouldBeFalse();
    }

    // What an unchosen project owns is outside, even a kind Squid never imports.
    [Fact]
    public void UnsupportedKindOwnedByUnchosenProject_OutsideSelection()
    {
        var graph = new OctopusResourceGraph([Node("Projects-1", OctopusResourceKind.Project, "Projects-1"), Node("Projects-2", OctopusResourceKind.Project, "Projects-2"), Node("Runbooks-2", OctopusResourceKind.Runbook, "Projects-2")], [], [], []);

        OctopusImportReferenceScope.Build(graph, ["Projects-1"]).IsOutsideSelection(graph.Resources[2]).ShouldBeTrue();
    }

    [Fact]
    public async Task NoSelection_NothingOutside_ScopeIsTheWholeSpaceRule()
    {
        var graph = await Space.BuildGraphAsync();
        var scope = OctopusImportReferenceScope.Build(graph);

        graph.Resources.ShouldAllBe(r => !scope.IsOutsideSelection(r));
        scope.IsReferenced("Projects-2").ShouldBeTrue();
        scope.IsReferenced("Feeds-3").ShouldBeFalse();
    }

    // An export file's ids carry its suffix; the selection matches either spelling.
    [Fact]
    public async Task ExportSuffixedChoice_MatchesTheBareProjectId()
    {
        var scope = await ScopeAsync("Projects-1-00112233445566778899AABBCCDDEEFF");

        scope.IsOutsideSelection(await NodeAsync("Projects-1")).ShouldBeFalse();
        scope.IsOutsideSelection(await NodeAsync("Environments-1")).ShouldBeFalse();
        scope.IsOutsideSelection(await NodeAsync("Projects-2")).ShouldBeTrue();
    }

    [Fact]
    public void BareChoice_MatchesTheExportSuffixedProjectId()
    {
        var graph = new OctopusResourceGraph([Node("Projects-1-00112233445566778899AABBCCDDEEFF", OctopusResourceKind.Project, "Projects-1-00112233445566778899AABBCCDDEEFF")], [], [], []);

        OctopusImportReferenceScope.Build(graph, ["projects-1"]).IsOutsideSelection(graph.Resources[0]).ShouldBeFalse();
    }

    // Octopus deploys a lifecycle without phases to every environment, so a project on one uses them all.
    [Fact]
    public async Task ChosenProjectOnPhaselessLifecycle_UsesEveryEnvironment()
    {
        var space = new OctopusImportScenario().ProjectOnLifecycle("Projects-1", "Web", "Lifecycles-9").PhaselessLifecycle("Lifecycles-9", "Default Lifecycle").Environment("Environments-1", "Dev").Environment("Environments-2", "Prod");
        var graph = await space.BuildGraphAsync();
        var scope = OctopusImportReferenceScope.Build(graph, ["Projects-1"]);

        new[] { "Lifecycles-9", "Environments-1", "Environments-2" }.ShouldAllBe(id => !scope.IsOutsideSelection(graph.Resources.First(r => r.SourceId == id)));
    }

    // Defensive: a reference from a chosen project's resource into another project never brings that project in.
    [Fact]
    public void ReferenceIntoAnUnchosenProject_NeverEntersIt()
    {
        var resources = new[] { Node("Projects-1", OctopusResourceKind.Project, "Projects-1"), Node("Channels-1", OctopusResourceKind.Channel, "Projects-1"), Node("Projects-2", OctopusResourceKind.Project, "Projects-2"), Node("Channels-2", OctopusResourceKind.Channel, "Projects-2") };
        var references = new[] { new OctopusResourceReference("Channels-1", OctopusResourceKind.Channel, OctopusResourceReferenceKind.Project, "Projects-2", OctopusResourceKind.Project, "Projects-1", true, false), new OctopusResourceReference("Channels-1", OctopusResourceKind.Channel, OctopusResourceReferenceKind.Project, "Channels-2", OctopusResourceKind.Channel, "Projects-1", true, false) };
        var graph = new OctopusResourceGraph(resources, references, [], []);

        var scope = OctopusImportReferenceScope.Build(graph, ["Projects-1"]);

        scope.IsOutsideSelection(resources[1]).ShouldBeFalse();
        scope.IsOutsideSelection(resources[2]).ShouldBeTrue();
        scope.IsOutsideSelection(resources[3]).ShouldBeTrue();
    }

    // An account is named by value (a step property, a variable), not by a graph reference. Projects-1 names Accounts-1 in a step
    // property and Accounts-2 in a variable; Accounts-3 only Projects-2 names; Accounts-4 nobody. Teams-1 approves Projects-1's manual
    // step, Teams-2 only Projects-2's.
    private static readonly OctopusImportScenario AccountSpace = new OctopusImportScenario()
        .Project("Projects-1", "Web", [], [Var("web-var", "AwsAccount", "Accounts-2")], ScriptAction("azure", "echo", new() { ["Octopus.Action.Azure.AccountId"] = "Accounts-1" }), ManualAction("approve", "Teams-1"))
        .Project("Projects-2", "Api", [], [Var("api-var", "Account", "Accounts-3")], ManualAction("approve-api", "Teams-2"))
        .Account("Accounts-1", "Azure", "AzureServicePrincipal", "Environments-1")
        .Account("Accounts-2", "Aws", "AmazonWebServicesAccount")
        .Account("Accounts-3", "Api token", "Token")
        .Account("Accounts-4", "Unused", "Token")
        .Environment("Environments-1", "Dev")
        .Team("Teams-1", "Approvers")
        .Team("Teams-2", "Api approvers");

    [Theory]
    [InlineData("Accounts-1")]
    [InlineData("Accounts-2")]
    [InlineData("Environments-1")]
    [InlineData("Teams-1")]
    public async Task ChooseFirst_AccountsAndTeamsItNames_InScope(string sourceId)
    {
        var graph = await AccountSpace.BuildGraphAsync();

        OctopusImportReferenceScope.Build(graph, ["Projects-1"]).IsOutsideSelection(graph.Resources.First(r => r.SourceId == sourceId)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Accounts-3")]
    [InlineData("Accounts-4")]
    [InlineData("Teams-2")]
    public async Task ChooseFirst_AccountsAndTeamsOnlyOthersName_OutsideSelection(string sourceId)
    {
        var graph = await AccountSpace.BuildGraphAsync();

        OctopusImportReferenceScope.Build(graph, ["Projects-1"]).IsOutsideSelection(graph.Resources.First(r => r.SourceId == sourceId)).ShouldBeTrue();
    }

    // The account edges exist only with a selection: D4 still judges every account as unused (unchanged).
    [Fact]
    public async Task NoSelection_AccountsNamedByValue_StillUnreferenced()
    {
        var scope = OctopusImportReferenceScope.Build(await AccountSpace.BuildGraphAsync());

        scope.IsReferenced("Accounts-1").ShouldBeFalse();
        scope.IsReferenced("Accounts-2").ShouldBeFalse();
    }

    [Fact]
    public async Task DependencyPlanner_PassesTheSelectionToTheScope()
    {
        var graph = await Space.BuildGraphAsync();

        var plan = new OctopusImportDependencyPlanner().BuildCurrentConfigurationPlan(graph, ["Projects-1"]);

        plan.ReferenceScope.IsOutsideSelection(graph.Resources.First(r => r.SourceId == "Projects-2")).ShouldBeTrue();
    }

    private static OctopusResourceNode Node(string id, OctopusResourceKind kind, string ownerProjectId)
        => new(id, id, kind, OctopusDocumentKind.Unknown, $"{id}.json", ownerProjectId, null, false, null);

    private static async Task<OctopusImportReferenceScope> ScopeAsync(params string[] chosen) => OctopusImportReferenceScope.Build(await Space.BuildGraphAsync(), chosen);

    private static async Task<OctopusResourceNode> NodeAsync(string sourceId) => (await Space.BuildGraphAsync()).Resources.First(r => r.SourceId == sourceId);
}
