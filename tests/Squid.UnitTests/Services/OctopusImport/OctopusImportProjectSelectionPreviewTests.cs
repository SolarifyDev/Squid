using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using static Squid.UnitTests.Services.OctopusImport.OctopusImportScenario;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// A project selection through the REAL extraction → graph → dependency planner → discovery → preview planner → validator chain. A
/// resource outside the selection is skipped before every other decision: a project not chosen is never filled, refreshed, renamed or
/// offered a choice, its history is left as it is, and nothing it (or a shared resource no chosen project uses) holds blocks the import.
/// </summary>
public class OctopusImportProjectSelectionPreviewTests
{
    private const string Web = "Projects-1";
    private const string Api = "Projects-2";

    // Web deploys through Lifecycles-1 to Environments-1. Api uses a Maven feed (Squid cannot import one) and has history.
    private static OctopusImportScenario Space()
        => new OctopusImportScenario()
            .ExportedFromSpace(SuffixedExportSpaceId)
            .ProjectOnLifecycle(Web, "Web", "Lifecycles-1")
            .Lifecycle("Lifecycles-1", "Main", "Environments-1")
            .Environment("Environments-1", "Dev")
            .Project(Api, "Api", [], [], PackageScriptAction("deploy", "Feeds-9"))
            .Feed("Feeds-9", "Maven repo", "Maven")
            .Channel("Channels-2", Api, "Default")
            .Release("Releases-2", Api, "Channels-2", "1.0.0")
            .Deployment("Deployments-2", Api, "Channels-2", "Environments-1", "Releases-2", "ServerTasks-2")
            .ServerTask("ServerTasks-2");

    // ── Skipped before every other decision ────────────────────────────────

    [Fact]
    public async Task UnchosenProject_EverythingItOwns_SkippedAsNotChosen()
    {
        var plan = await Space().PlanAsync(chosenProjectIds: [Web]);

        plan.Resource(Api).ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip), r => r.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Skipped), r => r.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ProjectNotChosen), r => r.SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ProjectNotChosen));
        var owned = plan.Preview.Resources.Where(r => r.OwnerProjectSourceId == Api && r.SourceId != Api).ToList();
        owned.ShouldNotBeEmpty();
        owned.ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.Skip && r.Diagnostics.Single().Code == OctopusImportPreviewDiagnosticCodes.ProjectNotChosen);
        owned.Where(r => r.SourceType != "ServerTask").ShouldAllBe(r => r.Diagnostics.Single().Message.Contains("'Api'"));
        plan.Resource(Web).PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
    }

    // History of an unchosen project is never planned (no relink, no update, no "owner not imported").
    [Theory]
    [InlineData("Releases-2")]
    [InlineData("Deployments-2")]
    [InlineData("ServerTasks-2")]
    public async Task UnchosenProjectHistory_SkippedAsNotChosen_NotDecidedAsHistory(string sourceId)
    {
        var plan = await Space().PlanAsync(chosenProjectIds: [Web]);

        plan.Resource(sourceId).Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ProjectNotChosen);
    }

    [Fact]
    public async Task UnchosenProjectAlreadyImported_NotRefreshed_NoChoiceOffered()
    {
        var scenario = await ImportedWithDeletedChannelAsync();
        var whole = await scenario.PlanAsync();
        whole.Resource("Channels-2").Choice.ShouldNotBeNull("without a selection the deleted channel is offered");
        whole.Resource(Api).PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);

        var plan = await scenario.PlanAsync(chosenProjectIds: [Web]);

        plan.Resource(Api).PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        plan.Resource("Channels-2").ShouldSatisfyAllConditions(r => r.Choice.ShouldBeNull(), r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip), r => r.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ProjectNotChosen));
        plan.Preview.Summary.Choices.ShouldBeEmpty();
        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
    }

    [Fact]
    public async Task UnchosenProjectWithASquidNamesake_NoRenameAsked()
    {
        var scenario = Space().ExistingProject(77, "Api");

        (await scenario.PlanAsync()).Resource(Api).PreviewAction.ShouldBe(OctopusImportPreviewAction.RenameRequired);

        var plan = await scenario.PlanAsync(chosenProjectIds: [Web]);

        plan.Resource(Api).PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        plan.Blockers.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.RenameRequiredForProject, Describe(plan));
    }

    // A secret of an unchosen project that Squid still misses is not filled from the file.
    [Fact]
    public async Task UnchosenProjectsMissingSecret_NotFilled()
    {
        var scenario = ImportedSpaceMissingASecret().ProjectOnLifecycle("Projects-9", "New", "Lifecycles-9").Lifecycle("Lifecycles-9", "New", "Environments-1");
        (await scenario.PlanAsync(FileSecrets)).Resource(SecretId).PreviewAction.ShouldBe(OctopusImportPreviewAction.FillMissingSecret);

        var plan = await scenario.PlanAsync(FileSecrets, chosenProjectIds: ["Projects-9"]);

        plan.Resource(SecretId).PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        plan.Preview.Summary.Counts.Fill.ShouldBe(0);
    }

    // The validator asks for no fill evidence on a secret the selection leaves out.
    [Fact]
    public async Task UnchosenProjectsMissingSecret_NoFillEvidenceDemanded()
    {
        var scenario = ImportedSpaceMissingASecret(structureRecorded: false).ExportedFromSpace($"Spaces-1-{OtherFingerprint}").ProjectOnLifecycle("Projects-9", "New", "Lifecycles-9").Lifecycle("Lifecycles-9", "New", "Environments-1");
        (await scenario.PlanAsync(FileSecrets, pairingConfirmed: true)).Validation.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillInstallationUnproven && d.SourceId == SecretId);

        var plan = await scenario.PlanAsync(FileSecrets, pairingConfirmed: true, chosenProjectIds: ["Projects-9"]);

        plan.Validation.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillInstallationUnproven && d.SourceId == SecretId);
    }

    [Fact]
    public async Task DependencyBlockerInsideUnchosenProject_DoesNotBlock()
    {
        var plan = await Space().PlanAsync(chosenProjectIds: [Web]);
        var blocked = plan.DependencyPlan with { Diagnostics = [new OctopusInputExtractionDiagnostic(OctopusImportCompatibilitySeverity.Blocker, OctopusInputExtractionDiagnosticCodes.DependencyCycle, "cycle", SourceId: "Channels-2")] };

        var preview = new OctopusImportPreviewPlanner().BuildPreviewPlan(blocked, plan.Conflicts);

        preview.HasBlockers.ShouldBeFalse(string.Join("; ", preview.Diagnostics.Select(d => d.Code)));
    }

    [Fact]
    public async Task DependencyBlockerInsideChosenProject_StillBlocks()
    {
        var plan = await Space().PlanAsync(chosenProjectIds: [Api]);
        var blocked = plan.DependencyPlan with { Diagnostics = [new OctopusInputExtractionDiagnostic(OctopusImportCompatibilitySeverity.Blocker, OctopusInputExtractionDiagnosticCodes.DependencyCycle, "cycle", SourceId: "Channels-2")] };

        new OctopusImportPreviewPlanner().BuildPreviewPlan(blocked, plan.Conflicts).HasBlockers.ShouldBeTrue();
    }

    // ── Shared resources ───────────────────────────────────────────────────

    [Fact]
    public async Task UnsupportedFeedOnlyAnUnchosenProjectUses_NotUsedByChosenProjects_NotABlocker()
    {
        var plan = await Space().PlanAsync(chosenProjectIds: [Web]);

        var feed = plan.Resource("Feeds-9");
        feed.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        feed.Diagnostics.Single().ShouldSatisfyAllConditions(d => d.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.NotUsedByChosenProjects), d => d.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info), d => d.Message.ShouldBe(OctopusImportPreviewDiagnosticCodes.NotUsedByChosenProjectsMessage));
    }

    [Fact]
    public async Task UnsupportedFeedAChosenProjectUses_StillBlocks()
    {
        var plan = await Space().PlanAsync(chosenProjectIds: [Api]);

        plan.Resource("Feeds-9").PreviewAction.ShouldBe(OctopusImportPreviewAction.Blocked);
        plan.Resource("Lifecycles-1").Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.NotUsedByChosenProjects);
    }

    // An account is named by value (a step property), not by a graph reference: the chosen project's account is imported with the
    // environment it is scoped to; an account only the unchosen project names is left out.
    [Fact]
    public async Task AccountAChosenProjectsStepNames_Imported_WithItsEnvironment()
    {
        var scenario = new OctopusImportScenario()
            .Project(Web, "Web", [], [], ScriptAction("azure", "echo", new() { ["Octopus.Action.Azure.AccountId"] = "Accounts-1" }))
            .Project(Api, "Api", [], [], ScriptAction("api", "echo", new() { ["Octopus.Action.Azure.AccountId"] = "Accounts-2" }))
            .Account("Accounts-1", "Web token", "Token", "Environments-1")
            .Account("Accounts-2", "Api token", "Token")
            .Environment("Environments-1", "Dev");

        var plan = await scenario.PlanAsync(chosenProjectIds: [Web]);

        plan.Resource("Accounts-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        plan.Resource("Environments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        plan.Resource("Accounts-2").Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.NotUsedByChosenProjects);
        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
    }

    // Choosing every project is not "no selection": a shared resource no project uses is still left out.
    [Fact]
    public async Task EveryProjectChosen_UnusedSharedResourceSkipped_WhereTheWholeSpaceCreatesIt()
    {
        var scenario = Space().Feed("Feeds-3", "Spare registry", "Docker");

        (await scenario.PlanAsync()).Resource("Feeds-3").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        (await scenario.PlanAsync(chosenProjectIds: [Web, Api])).Resource("Feeds-3").Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.NotUsedByChosenProjects);
    }

    [Fact]
    public async Task MachinesAreReportedAsToday_NotAsUnusedByTheChosenProjects()
    {
        var plan = await Space().Machine("Machines-1", "web-01", "web").PlanAsync(chosenProjectIds: [Web]);

        plan.Resource("Machines-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Unsupported);
    }

    // ── Plan-level view ────────────────────────────────────────────────────

    [Fact]
    public async Task GroupedDiagnostics_OneInfoLinePerUnusedKind_AndTheProjectsNotChosen()
    {
        var plan = await Space().Feed("Feeds-3", "Spare registry", "Docker").PlanAsync(chosenProjectIds: [Web]);

        var unused = plan.Preview.Diagnostics.Where(d => d.Code == OctopusImportPreviewDiagnosticCodes.ResourcesNotUsedByChosenProjects).ToList();
        unused.ShouldHaveSingleItem().ShouldSatisfyAllConditions(d => d.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info), d => d.ResourceType.ShouldBe("Feed"), d => d.Message.ShouldBe("2 Octopus Feed resource(s) are not imported because no chosen project uses them: Maven repo, Spare registry."));
        plan.Preview.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.ProjectsNotChosen).ShouldSatisfyAllConditions(d => d.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info), d => d.Message.ShouldBe("1 Octopus project(s) were not chosen and are not imported."));
    }

    [Fact]
    public async Task Summary_ListsTheChosenProjectsOnly_CountsTheOthers()
    {
        var plan = await Space().PlanAsync(chosenProjectIds: [Web]);

        plan.Preview.Summary.Projects.Select(p => p.SourceId).ShouldBe([Web]);
        plan.Preview.Summary.NotChosenProjectCount.ShouldBe(1);
        plan.Preview.Summary.SkipReasons.ShouldContain(s => s.Code == OctopusImportPreviewDiagnosticCodes.ProjectNotChosen && s.ResourceType == "Project" && s.Count == 1);
    }

    [Fact]
    public async Task Summary_NoSelection_NoProjectCountedAsNotChosen()
        => (await Space().PlanAsync()).Preview.Summary.NotChosenProjectCount.ShouldBe(0);

    // Re-importing the same choice changes nothing: the rows left out are skips, not changes.
    [Fact]
    public async Task SameChoiceImportedAgain_UpToDate()
    {
        var first = await Space().PlanAsync(chosenProjectIds: [Web]);

        var again = await Space().ImportedAs(first).PlanAsync(chosenProjectIds: [Web]);

        again.Preview.Summary.IsUpToDate.ShouldBeTrue(Describe(again));
    }

    [Fact]
    public void SelectionCodes_Pinned()
    {
        OctopusImportPreviewDiagnosticCodes.ProjectNotChosen.ShouldBe("octopus.preview.project_not_chosen");
        OctopusImportPreviewDiagnosticCodes.NotUsedByChosenProjects.ShouldBe("octopus.preview.not_used_by_chosen_projects");
        OctopusImportPreviewDiagnosticCodes.ResourcesNotUsedByChosenProjects.ShouldBe("octopus.preview.resources_not_used_by_chosen_projects");
        OctopusImportPreviewDiagnosticCodes.ProjectsNotChosen.ShouldBe("octopus.preview.projects_not_chosen");
    }

    // ── A posted plan cannot widen the selection ───────────────────────────

    [Fact]
    public async Task PostedPlanTurnsAnUnchosenProjectIntoACreate_ServerRowWins()
    {
        var server = await Space().PlanAsync(chosenProjectIds: [Web]);
        var posted = await Space().PlanAsync();

        var merged = OctopusImportServerOwnedRows.MergeServerOwnedRows(posted.Preview, server.Preview);

        merged.Resources.Single(r => r.SourceId == Api).PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        merged.Resources.Single(r => r.SourceId == "Feeds-9").PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
    }

    [Fact]
    public async Task PostedPlanOmitsTheRowsLeftOut_ServerRowsAddedBack()
    {
        var server = await Space().PlanAsync(chosenProjectIds: [Web]);
        var posted = await Space().PlanAsync(chosenProjectIds: [Web]);
        posted.Preview.Resources = posted.Preview.Resources.Where(r => r.SourceId != Api).ToList();

        var merged = OctopusImportServerOwnedRows.MergeServerOwnedRows(posted.Preview, server.Preview);

        merged.Resources.Single(r => r.SourceId == Api).Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ProjectNotChosen);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private const string SecretId = "c4b1e0f2-0000-4000-8000-000000000001";
    private const string SecretActionId = "c4b1e0f2-0000-4000-8000-0000000000a1";
    private const string Fingerprint = "0123456789ABCDEF0123456789ABCDEF";
    private const string OtherFingerprint = "FEDCBA9876543210FEDCBA9876543210";
    private const int VariableSetDestination = 601;

    private static OctopusExportSecrets FileSecrets => OctopusExportSecrets.FromPassword(ExportPassword);

    // An API import left Web's sensitive variable Missing; the export file of the recorded origin carries it, Squid still holds it empty.
    private static OctopusImportScenario ImportedSpaceMissingASecret(bool structureRecorded = true)
    {
        var scope = Scope("Environment", "Environments-1");
        var scenario = new OctopusImportScenario()
            .Environment("Environments-1", "Test")
            .Project(Web, "Web", [], [Secret(SecretId, "Db.Password", Encrypt("db-secret"), scope)], ScriptAction(SecretActionId, "echo hi"))
            .ExistingMapping(OctopusResourceKind.Environment, "Environments-1", 301)
            .ExistingMapping(OctopusResourceKind.Project, Web, 501)
            .ExistingMapping(OctopusResourceKind.VariableSet, ProjectVariableSetId(Web), VariableSetDestination)
            .PriorInstallation("installation-a")
            .PriorExportSource(Fingerprint)
            .ExportedFromSpace($"Spaces-1-{Fingerprint}")
            .SquidSensitiveVariable(9001, VariableSetDestination, "Db.Password", false, (VariableScopeType.Environment, "301"));

        if (structureRecorded) scenario.ExistingMapping(OctopusResourceKind.DeploymentAction, SecretActionId, 701);

        return scenario.PriorRow(Squid.Core.Services.OctopusImport.SourceMap.OctopusImportSourceMapQueries.VariableSecretSourceType, scenario.VariableSecretKey(ProjectVariableSetId(Web), "Db.Password", scope), VariableSetDestination, Squid.Core.Services.OctopusImport.SourceMap.OctopusImportSourceMapQueries.MissingOutcome);
    }

    // Api was imported with its Hotfix channel, which was deleted in Squid since; Web is new in Octopus.
    private static async Task<OctopusImportScenario> ImportedWithDeletedChannelAsync()
    {
        static OctopusImportScenario Source() => new OctopusImportScenario().ExportedFromSpace(SuffixedExportSpaceId).Environment("Environments-1", "Dev").Project(Api, "Api", [], [], ScriptAction("Actions-2", "echo api")).Channel("Channels-2", Api, "Hotfix");

        var first = await Source().PlanAsync();

        return Source().ProjectOnLifecycle(Web, "Web", "Lifecycles-1").Lifecycle("Lifecycles-1", "Main", "Environments-1").ImportedAs(first).DeletedInSquid(OctopusResourceKind.Channel, "Channels-2");
    }

    private static string Describe(ScenarioResult plan)
        => string.Join("; ", plan.Blockers.Select(d => $"{d.Code}:{d.SourceId}:{d.Message}"));
}
