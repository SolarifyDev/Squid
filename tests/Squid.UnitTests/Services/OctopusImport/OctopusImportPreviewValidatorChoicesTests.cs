using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// The rows the user decides about are checked against the plan built again from Squid as it is (at validation and at confirmation):
/// whatever changed since the preview refuses the plan as stale instead of importing a row twice, and a forged choice or action is refused.
/// </summary>
public class OctopusImportPreviewValidatorChoicesTests
{
    private const string AddedStep = "Projects-1-step-2";

    [Fact]
    public async Task UnchangedSquid_SelectedPlan_Accepted()
    {
        var scenario = await StepScenarioAsync(deleted: true);
        var defaults = await scenario.PlanAsync();

        var plan = await scenario.PlanAsync(selections: Select(defaults.Preview, AddedStep, true));

        plan.Validation.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan);
        plan.BlocksConfirmation.ShouldBeFalse(string.Join(Environment.NewLine, plan.Blockers.Select(b => b.Message)));
    }

    [Fact]
    public async Task StepMadeAgainInSquidSinceThePreview_Stale()
    {
        var preview = await SelectedStepPlanAsync();
        var now = await StepScenarioAsync(deleted: true);
        now.SquidChild(OctopusResourceKind.DeploymentStep, now.PriorDestinationOf(OctopusResourceKind.DeploymentProcess, "deploymentprocess-Projects-1"), "Step 2");

        var stale = await ValidateAgainstAsync(now, preview);

        stale.ShouldContain("it is in Squid again");
    }

    [Fact]
    public async Task StepImportedByAnotherConfirmationSinceThePreview_Stale()
    {
        var preview = await SelectedStepPlanAsync();
        var now = (await StepScenarioAsync(deleted: true)).ExistingMapping(OctopusResourceKind.DeploymentStep, AddedStep, 7777);

        var stale = await ValidateAgainstAsync(now, preview);

        stale.ShouldContain("it was imported by another confirmation");
    }

    [Fact]
    public async Task ChoiceRowNoLongerInTheSource_Stale()
    {
        var preview = await SelectedStepPlanAsync();
        var first = await Web(withAddedStep: true).PlanAsync();
        var now = Web(withAddedStep: false).ImportedAs(first);

        var stale = await ValidateAgainstAsync(now, preview);

        stale.ShouldContain("not in the import source any more");
    }

    // Forged posts: an action that does not match the selection, another reason, or a choice on a row the server does not offer.
    [Theory]
    [InlineData("action")]
    [InlineData("reason")]
    [InlineData("choiceOnImportedRow")]
    [InlineData("createOnImportedRow")]
    public async Task ForgedChoiceOrAction_Stale(string forgery)
    {
        var scenario = await StepScenarioAsync(deleted: true);
        var preview = (await scenario.PlanAsync()).Preview;
        var step = preview.Resources.Single(r => r.SourceId == AddedStep);
        var imported = preview.Resources.Single(r => r.SourceId == "Projects-1-step-1");

        if (forgery == "action") step.PreviewAction = OctopusImportPreviewAction.Create;
        if (forgery == "reason") step.Choice.Reason = OctopusImportChoiceReason.AddedInOctopus;
        if (forgery == "choiceOnImportedRow") imported.Choice = new OctopusImportChoiceDto { Reason = OctopusImportChoiceReason.DeletedInSquid, Selected = true };
        if (forgery == "createOnImportedRow") imported.PreviewAction = OctopusImportPreviewAction.Create;

        var stale = await ValidateAgainstAsync(scenario, preview);

        stale.ShouldNotBeEmpty($"a forged {forgery} was accepted");
    }

    // Given the plan the server already built from the posted selection, the validator checks against it and never builds it
    // again; what it accepts and refuses is unchanged.
    [Theory]
    [InlineData(null)]
    [InlineData("action")]
    [InlineData("reason")]
    [InlineData("choiceOnImportedRow")]
    [InlineData("createOnImportedRow")]
    public async Task ServerPlanHandedOver_NotBuiltAgain_SameVerdict(string forgery)
    {
        var scenario = await StepScenarioAsync(deleted: true);
        var server = await scenario.PlanAsync();
        var posted = (await scenario.PlanAsync()).Preview;
        var step = posted.Resources.Single(r => r.SourceId == AddedStep);
        var imported = posted.Resources.Single(r => r.SourceId == "Projects-1-step-1");
        var planner = new Mock<IOctopusImportPreviewPlanner>(MockBehavior.Strict);

        if (forgery == "action") step.PreviewAction = OctopusImportPreviewAction.Create;
        if (forgery == "reason") step.Choice.Reason = OctopusImportChoiceReason.AddedInOctopus;
        if (forgery == "choiceOnImportedRow") imported.Choice = new OctopusImportChoiceDto { Reason = OctopusImportChoiceReason.DeletedInSquid, Selected = true };
        if (forgery == "createOnImportedRow") imported.PreviewAction = OctopusImportPreviewAction.Create;

        var validation = new OctopusImportPreviewValidator(previewPlanner: planner.Object).Validate(server.Graph, server.DependencyPlan, server.Conflicts, posted, server.Preview);
        var stale = validation.Diagnostics.Where(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan && d.Severity == OctopusImportCompatibilitySeverity.Blocker).ToList();

        (stale.Count > 0).ShouldBe(forgery != null, $"forgery '{forgery}': {string.Join(" | ", stale.Select(d => d.Message))}");
        (await ValidateAgainstAsync(scenario, posted)).Count.ShouldBe(stale.Count, "the handed-over plan must give the verdict a rebuilt plan gives");
    }

    // An old client's plan with nothing to decide on validates exactly as before.
    [Fact]
    public async Task NothingToDecide_NoChoiceDiagnostic()
    {
        var first = await Web(withAddedStep: true).PlanAsync();
        var repeat = await Web(withAddedStep: true).ImportedAs(first).PlanAsync();

        repeat.Preview.Resources.ShouldAllBe(r => r.Choice == null);
        repeat.Validation.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan);
    }

    // A selected variable is written like any other: one scoped to a channel the user did not select cannot keep its scope.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SelectedVariableScopedToAChannel_BlocksOnlyWhileTheChannelIsNotSelected(bool channelSelected, bool expectBlocker)
    {
        var first = await ScopedVariable(withAdditions: false).PlanAsync();
        var scenario = ScopedVariable(withAdditions: true).ImportedAs(first);
        var defaults = await scenario.PlanAsync();
        Select(defaults.Preview, "Channels-2", channelSelected);

        var plan = await scenario.PlanAsync(selections: Select(defaults.Preview, "Variables-2", true));

        plan.Validation.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnmappableVariableScope && d.SourceId == "Variables-2").ShouldBe(expectBlocker);
    }

    // A selected step is checked like any written step: a feed it uses that the import cannot map blocks; the same step not selected does not.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task SelectedStepUsingAnUnmappedFeed_Blocks_UnselectedDoesNot(bool selected, bool expectBlocker)
    {
        var first = await PackageStep(withAddedStep: false).PlanAsync();
        var scenario = PackageStep(withAddedStep: true).ImportedAs(first);
        var defaults = await scenario.PlanAsync();

        var plan = await scenario.PlanAsync(selections: Select(defaults.Preview, AddedStep, selected));

        plan.Validation.Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker && d.SourceId == "Actions-2").ShouldBe(expectBlocker, string.Join(Environment.NewLine, plan.Blockers.Select(b => $"{b.Code} {b.SourceId}")));
    }

    // ── Scenarios ──────────────────────────────────────────────────────────

    private static async Task<OctopusImportScenario> StepScenarioAsync(bool deleted)
    {
        var first = await Web(withAddedStep: deleted).PlanAsync();
        var scenario = Web(withAddedStep: true).ImportedAs(first);

        return deleted ? scenario.DeletedInSquid(OctopusResourceKind.DeploymentStep, AddedStep).DeletedInSquid(OctopusResourceKind.DeploymentAction, "Actions-2") : scenario;
    }

    private static async Task<OctopusImportPreviewPlanDto> SelectedStepPlanAsync()
    {
        var scenario = await StepScenarioAsync(deleted: true);
        var defaults = await scenario.PlanAsync();

        return (await scenario.PlanAsync(selections: Select(defaults.Preview, AddedStep, true))).Preview;
    }

    private static OctopusImportScenario Web(bool withAddedStep)
        => Base().Project("Projects-1", "Web", [], [], withAddedStep ? [Script("Actions-1"), Script("Actions-2")] : [Script("Actions-1")]).Channel("Channels-1", "Projects-1", "Main");

    private static OctopusImportScenario ScopedVariable(bool withAdditions)
    {
        var variables = withAdditions ? new[] { OctopusImportScenario.Var("Variables-1", "Greeting", "hello"), OctopusImportScenario.Var("Variables-2", "Hotfix.Only", "x", OctopusImportScenario.Scope("Channel", "Channels-2")) } : [OctopusImportScenario.Var("Variables-1", "Greeting", "hello")];
        var scenario = Base().Project("Projects-1", "Web", [], variables, Script("Actions-1")).Channel("Channels-1", "Projects-1", "Main");

        return withAdditions ? scenario.Channel("Channels-2", "Projects-1", "Hotfix") : scenario;
    }

    private static OctopusImportScenario PackageStep(bool withAddedStep)
        => Base().Project("Projects-1", "Web", [], [], withAddedStep ? [Script("Actions-1"), OctopusImportScenario.PackageScriptAction("Actions-2", "Feeds-missing")] : [Script("Actions-1")]).Channel("Channels-1", "Projects-1", "Main");

    private static OctopusImportScenario Base() => new OctopusImportScenario().Environment("Environments-1", "Production").ExportedFromSpace(OctopusImportScenario.SuffixedExportSpaceId);

    private static object Script(string id) => OctopusImportScenario.ScriptAction(id, $"echo {id}");

    private static OctopusImportChoiceSelections Select(OctopusImportPreviewPlanDto plan, string sourceId, bool selected)
    {
        plan.Resources.Single(r => r.SourceId == sourceId).Choice.Selected = selected;

        return OctopusImportChoiceSelections.From(plan);
    }

    // The stale messages a posted plan gets against Squid as the scenario now has it.
    private static async Task<List<string>> ValidateAgainstAsync(OctopusImportScenario now, OctopusImportPreviewPlanDto posted)
    {
        var fresh = await now.PlanAsync();
        var validation = new OctopusImportPreviewValidator().Validate(fresh.Graph, fresh.DependencyPlan, fresh.Conflicts, posted);

        return validation.Diagnostics.Where(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan && d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => d.Message).ToList();
    }
}

internal static class StaleMessagesExtensions
{
    public static void ShouldContain(this List<string> messages, string fragment) => messages.ShouldContain(m => m.Contains(fragment), $"no stale message says '{fragment}': {string.Join(" | ", messages)}");
}
