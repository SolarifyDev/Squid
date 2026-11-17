using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// The resources a re-import offers for the user to decide on, through the REAL extraction → graph → discovery → planner chain: a child an
/// earlier import created that was deleted in Squid since, or a child added in Octopus after its owner was imported. The source map tells
/// the reason, the policy the default, and the selection (an input to the plan) only ever turns a choosable row into Create or Skip.
/// </summary>
public class OctopusImportPreviewPlannerChoiceTests
{
    private const string AddedStep = "Projects-1/step/Projects-1-step-2";

    public static IEnumerable<object[]> KindsAndReasons()
        => from kind in new[] { OctopusResourceKind.DeploymentStep, OctopusResourceKind.DeploymentAction, OctopusResourceKind.LifecyclePhase, OctopusResourceKind.Variable, OctopusResourceKind.Channel }
           from reason in Enum.GetValues<OctopusImportChoiceReason>()
           select new object[] { kind, reason };

    // ── Defaults and selection ─────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(KindsAndReasons))]
    public async Task ChoosableChild_Defaults_DeletedNeverRecreated_AddedOnlyAChannel(OctopusResourceKind kind, OctopusImportChoiceReason reason)
    {
        var plan = await (await ChildScenarioAsync(kind, reason)).PlanAsync();

        var row = plan.Resource(RootOf(kind));
        var expectedDefault = reason == OctopusImportChoiceReason.AddedInOctopus && kind == OctopusResourceKind.Channel;
        row.Choice.ShouldNotBeNull(Describe(plan));
        row.Choice.ShouldSatisfyAllConditions(c => c.Reason.ShouldBe(reason), c => c.DefaultSelected.ShouldBe(expectedDefault), c => c.Selected.ShouldBe(expectedDefault), c => c.FollowsSourceId.ShouldBeNull(), c => c.OwnerName.ShouldNotBeNullOrWhiteSpace());
        row.PreviewAction.ShouldBe(expectedDefault ? OctopusImportPreviewAction.Create : OctopusImportPreviewAction.Skip);
        row.Diagnostics.First().Code.ShouldBe(reason == OctopusImportChoiceReason.DeletedInSquid ? OctopusImportPreviewDiagnosticCodes.ChildDeletedInSquid : OctopusImportPreviewDiagnosticCodes.ChildAddedInOctopus);
        plan.BlocksConfirmation.ShouldBeFalse(Blockers(plan));
    }

    [Theory]
    [MemberData(nameof(KindsAndReasons))]
    public async Task ChoosableChild_SelectionFlipsCreateAndSkip_CountsFollowTheSelection(OctopusResourceKind kind, OctopusImportChoiceReason reason)
    {
        var scenario = await ChildScenarioAsync(kind, reason);
        var defaults = await scenario.PlanAsync();
        var flipped = !defaults.Resource(RootOf(kind)).Choice.DefaultSelected;

        var plan = await scenario.PlanAsync(selections: Select(defaults, RootOf(kind), flipped));

        var row = plan.Resource(RootOf(kind));
        row.Choice.Selected.ShouldBe(flipped);
        row.PreviewAction.ShouldBe(flipped ? OctopusImportPreviewAction.Create : OctopusImportPreviewAction.Skip);
        row.SkipReason?.Code.ShouldBe(reason == OctopusImportChoiceReason.DeletedInSquid ? OctopusImportPreviewDiagnosticCodes.ChildDeletedInSquid : OctopusImportPreviewDiagnosticCodes.ChildAddedInOctopus);
        plan.Preview.Summary.Choices.ShouldHaveSingleItem().ShouldSatisfyAllConditions(c => c.Reason.ShouldBe(reason), c => c.Total.ShouldBe(1), c => c.Selected.ShouldBe(flipped ? 1 : 0));
        plan.Preview.Summary.Counts.Create.ShouldBe(defaults.Preview.Summary.Counts.Create + (flipped ? 1 : -1) * (1 + FollowerCount(kind)), Describe(plan));
    }

    // A variable deleted in Squid before owner-qualified ids left only a bare-id row (the migration ties rows to owners through their Squid
    // rows): it is still offered as deleted in Squid, unless a clone in the source shares the bare id and nothing tells which one it was.
    [Theory]
    [InlineData(false, OctopusImportChoiceReason.DeletedInSquid)]
    [InlineData(true, OctopusImportChoiceReason.AddedInOctopus)]
    public async Task ChildDeletedBeforeOwnerQualifiedIds_BareRow_DeletedUnlessACloneSharesTheId(bool withClone, OctopusImportChoiceReason expectedReason)
    {
        var first = await Source(OctopusResourceKind.Variable, withChild: true).PlanAsync();
        var source = withClone ? Source(OctopusResourceKind.Variable, withChild: true).Project("Projects-2", "Web clone", [], [OctopusImportScenario.Var("Variables-2", "Added", "later")], Script("Actions-1")) : Source(OctopusResourceKind.Variable, withChild: true);
        var scenario = source.ImportedAs(first).WithoutMapping(OctopusResourceKind.Variable, RootOf(OctopusResourceKind.Variable)).DeletedInSquid(OctopusResourceKind.Variable, "Variables-2");

        var plan = await scenario.PlanAsync();

        var row = plan.Resource(RootOf(OctopusResourceKind.Variable));
        row.Choice.ShouldNotBeNull(Describe(plan));
        row.Choice.ShouldSatisfyAllConditions(c => c.Reason.ShouldBe(expectedReason), c => c.Selected.ShouldBe(false));
    }

    // A flag on a row the server does not offer is never read: an AlreadyImported row cannot be turned into a create by the client.
    [Fact]
    public async Task SelectionOnARowThatIsNotOffered_ChangesNothing()
    {
        var scenario = await ChildScenarioAsync(OctopusResourceKind.DeploymentStep, OctopusImportChoiceReason.AddedInOctopus);
        var defaults = await scenario.PlanAsync();
        var forged = new OctopusImportPreviewPlanDto { Resources = new[] { "Projects-1", "Projects-1/step/Projects-1-step-1", "Projects-1/action/Actions-1" }.Select(id => new OctopusImportResourceResultDto { SourceId = id, Choice = new OctopusImportChoiceDto { Selected = true } }).ToList() };

        var plan = await scenario.PlanAsync(selections: OctopusImportChoiceSelections.From(forged));

        foreach (var id in new[] { "Projects-1", "Projects-1/step/Projects-1-step-1", "Projects-1/action/Actions-1" })
            plan.Resource(id).ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported), r => r.Choice.ShouldBeNull());

        plan.Preview.Summary.Counts.ShouldBeEquivalentTo(defaults.Preview.Summary.Counts);
    }

    // The actions of a choosable step follow it: they carry its choice and have no selection of their own.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActionsOfAChoosableStep_FollowTheStep_TheirOwnFlagIgnored(bool stepSelected)
    {
        var scenario = await ChildScenarioAsync(OctopusResourceKind.DeploymentStep, OctopusImportChoiceReason.DeletedInSquid);
        var defaults = await scenario.PlanAsync();
        var posted = defaults.Preview;
        posted.Resources.Single(r => r.SourceId == AddedStep).Choice.Selected = stepSelected;
        posted.Resources.Single(r => r.SourceId == "Projects-1/action/Actions-2").Choice.Selected = !stepSelected;

        var plan = await scenario.PlanAsync(selections: OctopusImportChoiceSelections.From(posted));

        var action = plan.Resource("Projects-1/action/Actions-2");
        action.Choice.ShouldSatisfyAllConditions(c => c.FollowsSourceId.ShouldBe(AddedStep), c => c.Selected.ShouldBe(stepSelected), c => c.Reason.ShouldBe(OctopusImportChoiceReason.DeletedInSquid));
        action.PreviewAction.ShouldBe(stepSelected ? OctopusImportPreviewAction.Create : OctopusImportPreviewAction.Skip);
        plan.Preview.Summary.Choices.Single().Total.ShouldBe(1, "a follower is never counted on its own");
    }

    // ── Not offered ────────────────────────────────────────────────────────

    // Saving an owner in Squid re-inserts its children with new ids: one the owner still holds by name is in Squid, never offered.
    [Theory]
    [InlineData(OctopusResourceKind.DeploymentStep, OctopusResourceKind.DeploymentProcess, "deploymentprocess-Projects-1", "Step 2")]
    [InlineData(OctopusResourceKind.DeploymentAction, OctopusResourceKind.DeploymentStep, "Projects-1/step/Projects-1-step-1", "Actions-2")]
    [InlineData(OctopusResourceKind.LifecyclePhase, OctopusResourceKind.Lifecycle, "Lifecycles-1", "Phases-2")]
    [InlineData(OctopusResourceKind.Variable, OctopusResourceKind.VariableSet, "variableset-Projects-1", "Added")]
    public async Task DeletedRowButStillInSquidByName_AlreadyImported_NotOffered(OctopusResourceKind kind, OctopusResourceKind ownerKind, string ownerSourceId, string name)
    {
        var scenario = await ChildScenarioAsync(kind, OctopusImportChoiceReason.DeletedInSquid);
        scenario.SquidChild(kind, scenario.PriorDestinationOf(ownerKind, ownerSourceId), name);

        var plan = await scenario.PlanAsync();

        plan.Resource(RootOf(kind)).ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported, Describe(plan)), r => r.Choice.ShouldBeNull());
        plan.Preview.Summary.Choices.ShouldBeEmpty();
    }

    // Decision 3 is unchanged: a channel added in Octopus with a Squid namesake is linked, never a choice.
    [Fact]
    public async Task AddedChannelWithASquidNamesake_Linked_NotOffered()
    {
        var scenario = await ChildScenarioAsync(OctopusResourceKind.Channel, OctopusImportChoiceReason.AddedInOctopus);
        scenario.ExistingChannel(900, scenario.PriorDestinationOf(OctopusResourceKind.Project, "Projects-1"), "Hotfix");

        var plan = await scenario.PlanAsync();

        plan.Resource("Channels-2").ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.ReuseExisting), r => r.DestinationId.ShouldBe(900), r => r.Choice.ShouldBeNull());
    }

    // Squid's child names are read only for an imported owner with an incoming child that has no live row, once per kind.
    [Fact]
    public async Task ChildNames_ReadOnlyForOwnersWithAChildWithoutARow_NeverOnAnUnchangedRepeat()
    {
        var first = await Source(OctopusResourceKind.DeploymentStep, withChild: true).PlanAsync();
        var repeat = Source(OctopusResourceKind.DeploymentStep, withChild: true).ImportedAs(first);
        var added = await ChildScenarioAsync(OctopusResourceKind.DeploymentStep, OctopusImportChoiceReason.AddedInOctopus);

        await repeat.PlanAsync();
        await added.PlanAsync();

        repeat.ChildNameReads.ShouldBeEmpty();
        added.ChildNameReads.ShouldHaveSingleItem().ShouldSatisfyAllConditions(r => r.Kind.ShouldBe(OctopusResourceKind.DeploymentStep), r => r.OwnerIds.ShouldBe([added.PriorDestinationOf(OctopusResourceKind.DeploymentProcess, "deploymentprocess-Projects-1")]));
    }

    // A variable ticked into a library variable set an earlier import created is written into that set, so it is checked like any
    // written variable: a sensitive value nothing can decrypt blocks it. Unticked, the library is kept as it is and nothing blocks.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task VariableAddedToAnImportedLibrary_TickedIsWritten_AndCheckedLikeAnyWrittenVariable(bool selected, bool expectBlocker)
    {
        var first = await Library(withSecret: false).PlanAsync();
        var scenario = Library(withSecret: true).ImportedAs(first);
        var defaults = await scenario.PlanAsync();

        var plan = await scenario.PlanAsync(selections: Select(defaults, "LibraryVariableSets-1/variable/Variables-L2", selected));

        plan.Resource("LibraryVariableSets-1/variable/Variables-L2").PreviewAction.ShouldBe(selected ? OctopusImportPreviewAction.Create : OctopusImportPreviewAction.Skip, Describe(plan));
        plan.Blockers.Any(b => b.Code == OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing && b.SourceId == "LibraryVariableSets-1/variable/Variables-L2").ShouldBe(expectBlocker, Blockers(plan));
    }

    // ── History on a choosable channel ─────────────────────────────────────

    [Theory]
    [InlineData(OctopusImportChoiceReason.DeletedInSquid)]
    [InlineData(OctopusImportChoiceReason.AddedInOctopus)]
    public async Task ChannelNotSelected_ItsHistorySkippedWithThatReason_NothingMovedOrCreated(OctopusImportChoiceReason reason)
    {
        var scenario = await HotfixHistoryScenarioAsync(reason);
        var defaults = await scenario.PlanAsync();

        var plan = await scenario.PlanAsync(selections: Select(defaults, "Channels-2", false));

        plan.Resource("Channels-2").PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        foreach (var id in new[] { "Releases-2", "Deployments-2" })
            plan.Resource(id).ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip, Describe(plan)), r => r.SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.HistoryOwnerNotSelected), r => r.SkipReason.Message.ShouldContain("'Hotfix'"));
        plan.Resource("ServerTasks-2").PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
    }

    // A selected channel deleted in Squid is created again and the history imported on it moves onto it, exactly as before.
    [Fact]
    public async Task DeletedChannelSelected_RecreatedAndItsOrphanedHistoryRelinked()
    {
        var scenario = await HotfixHistoryScenarioAsync(OctopusImportChoiceReason.DeletedInSquid);
        var defaults = await scenario.PlanAsync();

        var plan = await scenario.PlanAsync(selections: Select(defaults, "Channels-2", true));

        plan.Resource("Channels-2").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        foreach (var id in new[] { "Releases-2", "Deployments-2" })
            plan.Resource(id).ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.Update, Describe(plan)), r => r.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.HistoryRelinked));
    }

    // ── Wording and summary ────────────────────────────────────────────────

    [Theory]
    [InlineData(OctopusImportChoiceReason.DeletedInSquid, false, OctopusImportCompatibilitySeverity.Info, "stays deleted")]
    [InlineData(OctopusImportChoiceReason.DeletedInSquid, true, OctopusImportCompatibilitySeverity.Info, "created again")]
    [InlineData(OctopusImportChoiceReason.AddedInOctopus, false, OctopusImportCompatibilitySeverity.Warning, "never changed unless you tick it")]
    [InlineData(OctopusImportChoiceReason.AddedInOctopus, true, OctopusImportCompatibilitySeverity.Info, "nothing already in Squid is changed or reordered")]
    public async Task ChoiceDiagnostic_NamesTheReasonAndWhatHappens(OctopusImportChoiceReason reason, bool selected, OctopusImportCompatibilitySeverity severity, string says)
    {
        var scenario = await ChildScenarioAsync(OctopusResourceKind.DeploymentStep, reason);
        var defaults = await scenario.PlanAsync();

        var plan = await scenario.PlanAsync(selections: Select(defaults, AddedStep, selected));

        var diagnostic = plan.Resource(AddedStep).Diagnostics.First();
        diagnostic.Severity.ShouldBe(severity);
        diagnostic.Message.ShouldContain(says);
        if (!selected) diagnostic.Message.ShouldContain("offered again on every import");
        if (reason == OctopusImportChoiceReason.AddedInOctopus) diagnostic.Message.ShouldContain("Deployment process of 'Web'");
    }

    // Nothing is written for an offer the user declines, so a space with only declined offers is up to date; the offers stay listed.
    [Fact]
    public async Task OnlyDeclinedOffersLeft_UpToDate_OffersStillListed()
    {
        var plan = await (await ChildScenarioAsync(OctopusResourceKind.DeploymentStep, OctopusImportChoiceReason.AddedInOctopus)).PlanAsync();

        plan.Preview.Summary.IsUpToDate.ShouldBeTrue(Describe(plan));
        plan.Preview.Summary.Choices.ShouldHaveSingleItem().ShouldSatisfyAllConditions(c => c.Total.ShouldBe(1), c => c.Selected.ShouldBe(0));
    }

    // Octopus variables may share a name and differ by scope: each added one is offered on its own, so ticking several adds each.
    [Fact]
    public async Task VariablesSharingAName_AddedInOctopus_EachOfferedOnItsOwn()
    {
        var plan = await (await ScopedVariablesScenarioAsync()).PlanAsync();

        foreach (var id in new[] { "Projects-1/variable/Variables-3", "Projects-1/variable/Variables-4" })
            plan.Resource(id).Choice.ShouldNotBeNull(Describe(plan)).Reason.ShouldBe(OctopusImportChoiceReason.AddedInOctopus);
    }

    // A Squid row holds at most one Octopus variable: the one ticked before (linked by the source map) never hides its namesake, and an
    // unlinked Squid row of the same name and scope holds that variable, not the other one.
    [Theory]
    [InlineData(true, "Projects-1/variable/Variables-4")]
    [InlineData(false, "Projects-1/variable/Variables-3")]
    public async Task VariablesSharingAName_ASquidRowHoldsOnlyOne_TheOtherStillOffered(bool linkedToProduction, string offered)
    {
        var scenario = await ScopedVariablesScenarioAsync();
        var setId = scenario.PriorDestinationOf(OctopusResourceKind.VariableSet, OctopusImportScenario.ProjectVariableSetId("Projects-1"));
        var environment = scenario.PriorDestinationOf(OctopusResourceKind.Environment, linkedToProduction ? "Environments-1" : "Environments-2").ToString();
        scenario.SquidVariable(3003, setId, "ApiUrl", (Squid.Message.Enums.VariableScopeType.Environment, environment));
        if (linkedToProduction) scenario.ExistingMapping(OctopusResourceKind.Variable, "Projects-1/variable/Variables-3", 3003);

        var plan = await scenario.PlanAsync();

        var kept = offered == "Projects-1/variable/Variables-3" ? "Projects-1/variable/Variables-4" : "Projects-1/variable/Variables-3";
        plan.Resource(kept).ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported, Describe(plan)), r => r.Choice.ShouldBeNull());
        plan.Resource(offered).Choice.ShouldNotBeNull(Describe(plan)).Reason.ShouldBe(OctopusImportChoiceReason.AddedInOctopus);
    }

    // ── Scenarios ──────────────────────────────────────────────────────────

    private static string RootOf(OctopusResourceKind kind)
        => kind switch
        {
            OctopusResourceKind.DeploymentStep => AddedStep,
            OctopusResourceKind.DeploymentAction => "Projects-1/action/Actions-2",
            OctopusResourceKind.LifecyclePhase => "Lifecycles-1/phase/Phases-2",
            OctopusResourceKind.Variable => "Projects-1/variable/Variables-2",
            _ => "Channels-2"
        };

    // The action of the added step follows it (counted with it when it is created).
    private static int FollowerCount(OctopusResourceKind kind) => kind == OctopusResourceKind.DeploymentStep ? 1 : 0;

    // Imported once (with the child when it is deleted in Squid since, without it when Octopus added it later), then planned again.
    private static async Task<OctopusImportScenario> ChildScenarioAsync(OctopusResourceKind kind, OctopusImportChoiceReason reason)
    {
        var deleted = reason == OctopusImportChoiceReason.DeletedInSquid;
        var first = await Source(kind, withChild: deleted).PlanAsync();
        var scenario = Source(kind, withChild: true).ImportedAs(first);

        if (!deleted) return scenario;

        scenario.DeletedInSquid(kind, RootOf(kind));

        return kind == OctopusResourceKind.DeploymentStep ? scenario.DeletedInSquid(OctopusResourceKind.DeploymentAction, "Projects-1/action/Actions-2") : scenario;
    }

    private static OctopusImportScenario Source(OctopusResourceKind kind, bool withChild)
    {
        var scenario = new OctopusImportScenario().Environment("Environments-1", "Production").ExportedFromSpace(OctopusImportScenario.SuffixedExportSpaceId);
        var actions = withChild ? new[] { Script("Actions-1"), Script("Actions-2") } : [Script("Actions-1")];

        return kind switch
        {
            OctopusResourceKind.DeploymentStep => scenario.Project("Projects-1", "Web", [], [], actions),
            OctopusResourceKind.DeploymentAction => scenario.ProjectWithOneStep("Projects-1", "Web", actions),
            OctopusResourceKind.LifecyclePhase => scenario.LifecycleWithPhases("Lifecycles-1", "Default", "Environments-1", withChild ? ["Phases-1", "Phases-2"] : ["Phases-1"]),
            OctopusResourceKind.Variable => scenario.Project("Projects-1", "Web", [], withChild ? [OctopusImportScenario.Var("Variables-1", "Greeting", "hello"), OctopusImportScenario.Var("Variables-2", "Added", "later")] : [OctopusImportScenario.Var("Variables-1", "Greeting", "hello")], Script("Actions-1")),
            _ => withChild ? scenario.Project("Projects-1", "Web", [], [], Script("Actions-1")).Channel("Channels-1", "Projects-1", "Main").Channel("Channels-2", "Projects-1", "Hotfix") : scenario.Project("Projects-1", "Web", [], [], Script("Actions-1")).Channel("Channels-1", "Projects-1", "Main")
        };
    }

    // Imported with one variable; Octopus then added "ApiUrl" twice, scoped to Production and to Staging.
    private static async Task<OctopusImportScenario> ScopedVariablesScenarioAsync()
    {
        var first = await ScopedVariables(withApiUrls: false).PlanAsync();

        return ScopedVariables(withApiUrls: true).ImportedAs(first);
    }

    private static OctopusImportScenario ScopedVariables(bool withApiUrls)
    {
        var greeting = OctopusImportScenario.Var("Variables-1", "Greeting", "hello");
        var variables = withApiUrls ? new[] { greeting, OctopusImportScenario.Var("Variables-3", "ApiUrl", "https://prod", OctopusImportScenario.Scope("Environment", "Environments-1")), OctopusImportScenario.Var("Variables-4", "ApiUrl", "https://staging", OctopusImportScenario.Scope("Environment", "Environments-2")) } : [greeting];

        return new OctopusImportScenario().Environment("Environments-1", "Production").Environment("Environments-2", "Staging").Project("Projects-1", "Web", [], variables, Script("Actions-1")).ExportedFromSpace(OctopusImportScenario.SuffixedExportSpaceId);
    }

    // A Hotfix channel with one release deployed on it: deleted in Squid since the import (its history left behind), or added in Octopus.
    private static async Task<OctopusImportScenario> HotfixHistoryScenarioAsync(OctopusImportChoiceReason reason)
    {
        var deleted = reason == OctopusImportChoiceReason.DeletedInSquid;
        var first = await HotfixHistory(withHotfix: deleted).PlanAsync();
        var scenario = HotfixHistory(withHotfix: true).ImportedAs(first);

        return deleted ? scenario.DeletedInSquid(OctopusResourceKind.Channel, "Channels-2").OrphanedInSquid(OctopusResourceKind.Release, "Releases-2").OrphanedInSquid(OctopusResourceKind.Deployment, "Deployments-2") : scenario;
    }

    private static OctopusImportScenario HotfixHistory(bool withHotfix)
    {
        var scenario = new OctopusImportScenario()
            .Environment("Environments-1", "Production")
            .Project("Projects-1", "Web", [], [], Script("Actions-1"))
            .Channel("Channels-1", "Projects-1", "Main")
            .ExportedFromSpace(OctopusImportScenario.SuffixedExportSpaceId);

        if (!withHotfix) return scenario;

        return scenario.Channel("Channels-2", "Projects-1", "Hotfix").Release("Releases-2", "Projects-1", "Channels-2", "2.0.0").Deployment("Deployments-2", "Projects-1", "Channels-2", "Environments-1", "Releases-2", "ServerTasks-2").ServerTask("ServerTasks-2");
    }

    private static object Script(string id) => OctopusImportScenario.ScriptAction(id, $"echo {id}");

    // A library variable set used by the project, with a sensitive variable (encrypted with a password this import does not have) added later.
    private static OctopusImportScenario Library(bool withSecret)
    {
        var variables = withSecret ? new[] { OctopusImportScenario.Var("Variables-L1", "Shared", "x"), OctopusImportScenario.Secret("Variables-L2", "Token", OctopusImportScenario.Encrypt("secret")) } : [OctopusImportScenario.Var("Variables-L1", "Shared", "x")];

        return new OctopusImportScenario().Environment("Environments-1", "Production").Library("LibraryVariableSets-1", "Common", variables).Project("Projects-1", "Web", ["LibraryVariableSets-1"], [], Script("Actions-1")).ExportedFromSpace(OctopusImportScenario.SuffixedExportSpaceId);
    }

    // The user's selection as the client posts it: the plan the server returned with one root's flag set.
    private static OctopusImportChoiceSelections Select(OctopusImportScenario.ScenarioResult plan, string sourceId, bool selected)
    {
        plan.Preview.Resources.Single(r => r.SourceId == sourceId).Choice.Selected = selected;

        return OctopusImportChoiceSelections.From(plan.Preview);
    }

    private static string Blockers(OctopusImportScenario.ScenarioResult plan) => string.Join(Environment.NewLine, plan.Blockers.Select(d => $"{d.Code} [{d.SourceId}] {d.Message}"));

    private static string Describe(OctopusImportScenario.ScenarioResult plan)
        => string.Join(Environment.NewLine, plan.Preview.Resources.Select(r => $"{r.SourceType} {r.SourceId} {r.PreviewAction} {r.Choice?.Reason}/{r.Choice?.Selected}/{r.Choice?.FollowsSourceId} {string.Join(",", r.Diagnostics.Select(d => d.Code))}"));
}
