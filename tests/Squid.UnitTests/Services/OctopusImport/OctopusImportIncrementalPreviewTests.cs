using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// The incremental preview through the REAL extraction → graph → discovery → planner chain: where each project's changes land in Squid
/// (an existing Squid project by id and name, or a new one), what already exists, what this import creates / updates / fills / skips per
/// kind and per project, the field-level old → new of every Update and Fill (secrets named only), why skipped resources are skipped, and
/// "everything up to date" when importing the same source again. Every plan is also checked for the summary's own invariants.
/// </summary>
public class OctopusImportIncrementalPreviewTests
{
    // ── First import ───────────────────────────────────────────────────────

    [Fact]
    public async Task FirstImport_ProjectIsNew_EverythingCreated_NotUpToDate()
    {
        var plan = await History().PlanAsync();

        ShouldBeConsistent(plan);
        var summary = plan.Preview.Summary;
        summary.IsUpToDate.ShouldBeFalse();
        summary.Counts.AlreadyImported.ShouldBe(0);
        summary.Counts.Create.ShouldBe(summary.Counts.Incremental);
        var project = summary.Projects.Single();
        project.ShouldSatisfyAllConditions(p => p.SourceId.ShouldBe("Projects-1"), p => p.SourceName.ShouldBe("Web"), p => p.Destination.State.ShouldBe(OctopusImportProjectDestinationState.New), p => p.Destination.ProjectId.ShouldBeNull(), p => p.IsUpToDate.ShouldBeFalse());
        KindCounts(project, "Release").Create.ShouldBe(1);
        KindCounts(project, "Deployment").Create.ShouldBe(1);
        KindCounts(project, "ServerTask").Create.ShouldBe(1);
        KindCounts(project, "Channel").Create.ShouldBe(1);
        summary.Space.Kinds.Select(k => k.Kind).ShouldBe(["Environment"]);
        summary.Space.Counts.Create.ShouldBe(1);
    }

    // ── Repeat import ──────────────────────────────────────────────────────

    // Same source again: nothing to write, every project on its Squid project, no diffs. File- and API-shaped documents alike.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondImportOfTheSameSource_AllAlreadyImported_UpToDate_ProjectsOnTheirSquidProject_NoChanges(bool restShaped)
    {
        var first = await Source(restShaped).PlanAsync();
        var scenario = Source(restShaped).ImportedAs(first);

        var second = await scenario.PlanAsync();

        ShouldBeConsistent(second);
        var summary = second.Preview.Summary;
        summary.IsUpToDate.ShouldBeTrue(Describe(second));
        summary.Counts.Incremental.ShouldBe(0);
        summary.Counts.Blocked.ShouldBe(0);
        summary.Counts.AlreadyImported.ShouldBe(first.Preview.Summary.Counts.Create);
        summary.Projects.ShouldAllBe(p => p.IsUpToDate && p.Destination.State == OctopusImportProjectDestinationState.Existing);
        var project = summary.Projects.Single();
        project.Destination.ProjectId.ShouldBe(scenario.PriorDestinationOf(OctopusResourceKind.Project, "Projects-1"));
        project.Destination.ProjectName.ShouldBe("Web");
        second.Preview.Resources.ShouldAllBe(r => r.Changes.Count == 0);
        scenario.StoredHistoryReads.ShouldBeEmpty("nothing changed, so no Squid row is read for a diff");
    }

    // The destination names the Squid project as it is now, not the Octopus name.
    [Fact]
    public async Task ImportedProjectRenamedInSquid_DestinationNamesTheSquidProject()
    {
        var plan = await new OctopusImportScenario()
            .Project("Projects-1", "Web", [], [])
            .ExistingProject(500, "Storefront (renamed in Squid)")
            .ExistingMapping(OctopusResourceKind.Project, "Projects-1", 500)
            .PlanAsync();

        ShouldBeConsistent(plan);
        plan.Preview.Summary.Projects.Single().Destination.ShouldSatisfyAllConditions(d => d.State.ShouldBe(OctopusImportProjectDestinationState.Existing), d => d.ProjectId.ShouldBe(500), d => d.ProjectName.ShouldBe("Storefront (renamed in Squid)"), d => d.ProjectSlug.ShouldBe("storefront (renamed in squid)"));
    }

    // ── Incremental ────────────────────────────────────────────────────────

    // A channel and a release added in Octopus later land on the existing Squid project.
    [Fact]
    public async Task NewChannelAndReleaseInOctopus_CreatedOnTheExistingSquidProject()
    {
        var first = await History().PlanAsync();
        var scenario = History().Channel("Channels-2", "Projects-1", "Hotfix").Release("Releases-2", "Projects-1", "Channels-2", "1.0.1").ImportedAs(first);

        var plan = await scenario.PlanAsync();

        ShouldBeConsistent(plan);
        var summary = plan.Preview.Summary;
        summary.IsUpToDate.ShouldBeFalse();
        summary.Counts.Create.ShouldBe(2, Describe(plan));
        summary.Counts.Incremental.ShouldBe(2);
        var project = summary.Projects.Single();
        project.Destination.State.ShouldBe(OctopusImportProjectDestinationState.Existing);
        project.Destination.ProjectId.ShouldBe(scenario.PriorDestinationOf(OctopusResourceKind.Project, "Projects-1"));
        project.IsUpToDate.ShouldBeFalse();
        KindCounts(project, "Channel").ShouldSatisfyAllConditions(c => c.Create.ShouldBe(1), c => c.AlreadyImported.ShouldBe(1));
        KindCounts(project, "Release").ShouldSatisfyAllConditions(c => c.Create.ShouldBe(1), c => c.AlreadyImported.ShouldBe(1));
        summary.Space.IsUpToDate.ShouldBeTrue();
    }

    // A variable and a step added in Octopus after the project was imported are not written into the existing Squid project unless the user
    // ticks them (imported configuration is never changed silently). The preview says so per resource and never counts them as "already in Squid".
    [Fact]
    public async Task VariableAndStepAddedInOctopusAfterTheImport_SkippedWithReason_NotCountedAsAlreadyInSquid()
    {
        var first = await History().PlanAsync();
        var scenario = new OctopusImportScenario()
            .Environment("Environments-1", "Production")
            .Project("Projects-1", "Web", [], [OctopusImportScenario.Var("Variables-1", "Greeting", "hello"), OctopusImportScenario.Secret("Variables-2", "Api.Key", null)], OctopusImportScenario.ScriptAction("Actions-1", "echo hello"), OctopusImportScenario.ScriptAction("Actions-2", "echo added"))
            .Channel("Channels-1", "Projects-1", "Main")
            .ExportedFromSpace(OctopusImportScenario.SuffixedExportSpaceId)
            .ImportedAs(first);

        var plan = await scenario.PlanAsync();

        ShouldBeConsistent(plan);
        plan.Resource("Projects-1/variable/Variables-2").ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip, Describe(plan)), r => r.SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ChildAddedInOctopus), r => r.DestinationId.ShouldBeNull());
        plan.Resource("Projects-1/step/Projects-1-step-2").SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ChildAddedInOctopus);
        plan.Resource("Projects-1/action/Actions-2").SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.SkippedWithOwner, "an action of the added step goes with it");
        plan.Resource("Projects-1/variable/Variables-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        plan.Resource("Projects-1/action/Actions-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        var project = plan.Preview.Summary.Projects.Single();
        KindCounts(project, "Variable").ShouldSatisfyAllConditions(c => c.AlreadyImported.ShouldBe(1), c => c.Skip.ShouldBe(1));
        KindCounts(project, "DeploymentStep").ShouldSatisfyAllConditions(c => c.AlreadyImported.ShouldBe(1), c => c.Skip.ShouldBe(1));
        plan.Preview.Summary.SkipReasons.Where(r => r.Code == OctopusImportPreviewDiagnosticCodes.ChildAddedInOctopus).Select(r => (r.ResourceType, r.Count)).OrderBy(r => r.ResourceType).ShouldBe([("DeploymentStep", 1), ("Variable", 1)]);
        plan.BlocksConfirmation.ShouldBeFalse();
    }

    [Fact]
    public async Task ReleaseNotesChangedInOctopus_UpdateShowsSquidNotesAndOctopusNotes()
    {
        var first = await History(notes: "first notes").PlanAsync();
        var scenario = History(notes: "edited in Octopus").ImportedAs(first);

        var plan = await scenario.PlanAsync();

        ShouldBeConsistent(plan);
        var change = plan.Resource("Releases-1").Changes.ShouldHaveSingleItem();
        change.ShouldSatisfyAllConditions(c => c.Field.ShouldBe(OctopusImportPreviewChanges.ReleaseNotesField), c => c.OldValue.ShouldBe("first notes"), c => c.NewValue.ShouldBe("edited in Octopus"), c => c.ValueHidden.ShouldBeFalse());
        plan.Preview.Summary.Counts.Update.ShouldBe(1);
        KindCounts(plan.Preview.Summary.Projects.Single(), "Release").Update.ShouldBe(1);
        scenario.StoredHistoryReads.ShouldHaveSingleItem().ShouldSatisfyAllConditions(r => r.ReleaseIds.ShouldBe([scenario.PriorDestinationOf(OctopusResourceKind.Release, "Releases-1")]), r => r.TaskIds.ShouldBeEmpty());
    }

    // Notes edited (or cleared) in Squid are the user's: a later Octopus change is not applied, the release is skipped with the reason and
    // its deployments still land on it.
    [Theory]
    [InlineData("first notes + approved by ops")]
    [InlineData("")]
    public async Task ReleaseNotesEditedInSquid_ThenChangedInOctopus_KeptAndSkippedWithReason_UpToDate(string squidNotes)
    {
        var first = await History(notes: "first notes").PlanAsync();
        var scenario = History(notes: "edited in Octopus").ImportedAs(first);
        var releaseId = scenario.PriorDestinationOf(OctopusResourceKind.Release, "Releases-1");
        scenario.StoredReleaseNotes(releaseId, squidNotes);

        var plan = await scenario.PlanAsync();

        ShouldBeConsistent(plan);
        plan.Resource("Releases-1").ShouldSatisfyAllConditions(
            r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip, Describe(plan)),
            r => r.DestinationId.ShouldBe(releaseId),
            r => r.SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ReleaseNotesEditedInSquid),
            r => r.Diagnostics.ShouldHaveSingleItem().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info));
        plan.Resource("Deployments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        plan.Preview.Summary.Counts.Update.ShouldBe(0);
        plan.Preview.Summary.IsUpToDate.ShouldBeTrue();
    }

    // Two Octopus servers share ids like Releases-1 and Deployments-1. An export file whose fingerprint the space does not know (or that
    // carries none), with no process id in common, would link to the history an import of the space's origin created: the import is
    // refused as a whole (preview and validation), so its history never overwrites what Squid holds. The recorded fingerprint updates.
    [Theory]
    [InlineData(OtherServerFingerprint, true)]
    [InlineData(null, true)]
    [InlineData(PinnedFingerprint, false)]
    public async Task HistoryChangedInAFileNotProvenToBeTheOrigin_RefusedAsAWhole_NeverOverwritten(string fileFingerprint, bool refused)
    {
        var first = await History(notes: "first notes", state: "Executing", finished: false).PlanAsync();
        var scenario = History(notes: "edited in Octopus").ImportedAs(first).PriorExportSource(PinnedFingerprint);

        scenario.ExportedFromSpace(fileFingerprint == null ? null : $"Spaces-1-{fileFingerprint}");

        var plan = await scenario.PlanAsync();

        ShouldBeConsistent(plan);
        plan.BlocksConfirmation.ShouldBe(refused, Describe(plan));
        plan.Blockers.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven).ShouldBe(refused, Describe(plan));
        plan.Validation.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven && d.Severity == OctopusImportCompatibilitySeverity.Blocker).ShouldBe(refused, "validation refuses on its own");
        foreach (var id in new[] { "Releases-1", "Deployments-1", "ServerTasks-1" })
            plan.Resource(id).PreviewAction.ShouldBe(OctopusImportPreviewAction.Update, $"{id}: {Describe(plan)}");
    }

    private const string PinnedFingerprint = "0123456789ABCDEF0123456789ABCDEF";
    private const string OtherServerFingerprint = "FEDCBA9876543210FEDCBA9876543210";

    // The old side is what Squid holds now; only fields that really differ are listed, on the deployment and its task alike.
    [Fact]
    public async Task TaskFinishedInOctopus_UpdateListsOnlyTheChangedFields_OldFromSquid()
    {
        var first = await History(state: "Executing", finished: false).PlanAsync();
        var scenario = History(state: "Success").ImportedAs(first);

        var plan = await scenario.PlanAsync();

        ShouldBeConsistent(plan);
        var deployment = plan.Resource("Deployments-1");
        deployment.PreviewAction.ShouldBe(OctopusImportPreviewAction.Update);
        deployment.Changes.Select(c => (c.Field, c.OldValue, c.NewValue)).ShouldBe([(OctopusImportPreviewChanges.StateField, "Executing", "Success"), (OctopusImportPreviewChanges.CompletedTimeField, null, "2026-01-02T03:05:06.000Z")]);
        plan.Resource("ServerTasks-1").Changes.Select(c => c.Field).ShouldBe(deployment.Changes.Select(c => c.Field));
        plan.Preview.Summary.Counts.Update.ShouldBe(2);
        scenario.StoredHistoryReads.ShouldHaveSingleItem().TaskIds.ShouldBe([scenario.PriorDestinationOf(OctopusResourceKind.ServerTask, "ServerTasks-1")]);
    }

    [Fact]
    public async Task TaskDetailsChangedInOctopus_UpdateNamesTheDetails()
    {
        var first = await History(detailFingerprint: "details-1").PlanAsync();
        var scenario = History(detailFingerprint: "details-2").ImportedAs(first).ExistingMapping(OctopusResourceKind.Deployment, "Deployments-1", 77, OctopusImportFingerprint.ForTask(OctopusImportScenario.TaskDocument("ServerTasks-1")), "details-1");

        var plan = await scenario.PlanAsync();

        ShouldBeConsistent(plan);
        plan.Resource("Deployments-1").Changes.ShouldHaveSingleItem().ShouldSatisfyAllConditions(c => c.Field.ShouldBe(OctopusImportPreviewChanges.DetailsField), c => c.Description.ShouldBe(OctopusImportPreviewChanges.DetailsChangedDescription));
    }

    [Fact]
    public async Task ImportedTaskGoneFromSquid_UpdateSaysItIsWrittenAgain()
    {
        var first = await History().PlanAsync();

        var plan = await History().ImportedAs(first).WithoutMapping(OctopusResourceKind.ServerTask, "ServerTasks-1").PlanAsync();

        ShouldBeConsistent(plan);
        plan.Resource("Deployments-1").Changes.ShouldHaveSingleItem().ShouldSatisfyAllConditions(c => c.Field.ShouldBe(OctopusImportPreviewChanges.TaskField), c => c.Description.ShouldBe(OctopusImportPreviewChanges.TaskRowMissingDescription));
    }

    // ── Skipped / blocked ──────────────────────────────────────────────────

    [Fact]
    public async Task SkippedResources_CarryTheirReason_SummaryGroupsReasonsPerKind()
    {
        var plan = await History().ServerTask("ServerTasks-runbook").Feed("Feeds-9", "Maven repo", "Maven").Feed("Feeds-10", "Other Maven", "Maven").Machine("Machines-1", "web-01", "web").PlanAsync();

        ShouldBeConsistent(plan);
        plan.Resource("ServerTasks-runbook").SkipReason.ShouldNotBeNull().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.TaskImportedWithDeployment);
        plan.Resource("Feeds-9").SkipReason.ShouldNotBeNull().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.UnreferencedResourceSkipped);
        plan.Resource("Releases-1").SkipReason.ShouldBeNull();
        var reasons = plan.Preview.Summary.SkipReasons;
        reasons.Single(r => r.ResourceType == "Feed").ShouldSatisfyAllConditions(r => r.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.UnreferencedResourceSkipped), r => r.Count.ShouldBe(2), r => r.Message.ShouldNotBeNullOrWhiteSpace());
        reasons.Single(r => r.ResourceType == "ServerTask").Count.ShouldBe(1);
        plan.Resource("Machines-1").SkipReason.ShouldNotBeNull().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ResourceUnsupported);
        plan.Preview.Summary.Counts.Skip.ShouldBe(3);
        plan.Preview.Summary.Counts.Unsupported.ShouldBe(1);
    }

    // Linking an existing Squid resource by name is a change (the source map learns it), so the plan is not up to date.
    [Fact]
    public async Task ExistingSquidResourceLinkedByName_CountedReused_NotUpToDate()
    {
        var plan = await new OctopusImportScenario().Environment("Environments-1", "Production").ExistingEnvironment(300, "Production").PlanAsync();

        ShouldBeConsistent(plan);
        plan.Resource("Environments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.ReuseExisting);
        plan.Preview.Summary.Counts.ShouldSatisfyAllConditions(c => c.Reused.ShouldBe(1), c => c.Incremental.ShouldBe(1));
        plan.Preview.Summary.Space.IsUpToDate.ShouldBeFalse();
        plan.Preview.Summary.IsUpToDate.ShouldBeFalse();
    }

    [Fact]
    public async Task ProjectNameTakenInSquid_NotImported_CountedBlocked_NotUpToDate()
    {
        var plan = await new OctopusImportScenario().Project("Projects-1", "Web", [], []).ExistingProject(500, "Web").PlanAsync();

        ShouldBeConsistent(plan);
        var project = plan.Preview.Summary.Projects.Single();
        project.Destination.ShouldSatisfyAllConditions(d => d.State.ShouldBe(OctopusImportProjectDestinationState.NotImported), d => d.ProjectId.ShouldBeNull());
        project.Counts.Blocked.ShouldBe(1);
        project.IsUpToDate.ShouldBeFalse();
        plan.Preview.Summary.IsUpToDate.ShouldBeFalse();
    }

    // History of a project an earlier import created, from a source without the project document, lands on that Squid project.
    [Fact]
    public async Task HistoryOfAProjectNotInTheSource_LandsOnTheProjectTheSourceMapRecords()
    {
        var plan = await new OctopusImportScenario()
            .Release("Releases-1", "Projects-1", "Channels-1", "1.0.0")
            .ExistingProject(500, "Web")
            .ExistingMapping(OctopusResourceKind.Project, "Projects-1", 500)
            .ExistingMapping(OctopusResourceKind.Channel, "Channels-1", 510)
            .PlanAsync();

        ShouldBeConsistent(plan);
        var project = plan.Preview.Summary.Projects.Single();
        project.SourceName.ShouldBeNull();
        project.Destination.ShouldSatisfyAllConditions(d => d.State.ShouldBe(OctopusImportProjectDestinationState.Existing), d => d.ProjectId.ShouldBe(500), d => d.ProjectName.ShouldBe("Web"));
        KindCounts(project, "Release").Create.ShouldBe(1);
    }

    // A plan-level blocker (history from another Octopus installation) is never "up to date", even when every resource is.
    [Fact]
    public async Task PlanLevelBlocker_NotUpToDate()
    {
        var first = await History().Installation("installation-a").PlanAsync();

        var plan = await History().Installation("installation-b").PriorInstallation("installation-a").ImportedAs(first).PlanAsync();

        ShouldBeConsistent(plan);
        plan.Preview.Summary.Counts.Incremental.ShouldBe(0);
        plan.Preview.Summary.IsUpToDate.ShouldBeFalse();
    }

    // The summary reads the decisions alone: a blocked or rename-required resource is never "up to date", whatever its diagnostics.
    [Theory]
    [InlineData(OctopusImportPreviewAction.Blocked, false)]
    [InlineData(OctopusImportPreviewAction.RenameRequired, false)]
    [InlineData(OctopusImportPreviewAction.AlreadyImported, true)]
    [InlineData(OctopusImportPreviewAction.Skip, true)]
    [InlineData(OctopusImportPreviewAction.Unsupported, true)]
    public void Summary_DecisionAlone_DecidesUpToDate(OctopusImportPreviewAction action, bool upToDate)
    {
        var resources = new List<OctopusImportResourceResultDto> { new() { SourceId = "Environments-1", SourceType = "Environment", PreviewAction = action } };

        var summary = OctopusImportPreviewSummaries.Build(resources, [], OctopusImportPriorMappings.Empty, hasPlanBlockers: false);

        summary.IsUpToDate.ShouldBe(upToDate);
        summary.Space.IsUpToDate.ShouldBe(upToDate);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static OctopusImportScenario History(string notes = "notes", string state = "Success", bool finished = true, string detailFingerprint = null)
    {
        var task = OctopusImportScenario.TaskDocument("ServerTasks-1", state, detailFingerprint);

        if (!finished) task.CompletedTime = null;

        return new OctopusImportScenario()
            .Environment("Environments-1", "Production")
            .Project("Projects-1", "Web", [], [OctopusImportScenario.Var("Variables-1", "Greeting", "hello")], OctopusImportScenario.ScriptAction("Actions-1", "echo hello"))
            .Channel("Channels-1", "Projects-1", "Main")
            .Release("Releases-1", "Projects-1", "Channels-1", "1.0.0", notes)
            .Deployment("Deployments-1", "Projects-1", "Channels-1", "Environments-1", "Releases-1", "ServerTasks-1")
            .ServerTaskDocument(task)
            .ExportedFromSpace(OctopusImportScenario.SuffixedExportSpaceId);
    }

    // The same space shaped as an export file, or as the server source stages it (variables and process through the real normaliser).
    private static OctopusImportScenario Source(bool restShaped)
    {
        var scenario = new OctopusImportScenario().Environment("Environments-1", "Production").Channel("Channels-1", "Projects-1", "Main").Release("Releases-1", "Projects-1", "Channels-1", "1.0.0", "notes");
        var variables = new object[] { OctopusImportScenario.Var("6f0b1c2d-0000-4000-8000-000000000001", "Greeting", "hello") };
        var action = OctopusImportScenario.ScriptAction("6f0b1c2d-0000-4000-8000-0000000000a1", "echo hello");

        return restShaped ? scenario.RestProject("Projects-1", "Web", [], variables, action) : scenario.Project("Projects-1", "Web", [], variables, action);
    }

    private static OctopusImportChangeCountsDto KindCounts(OctopusImportPreviewGroupDto group, string kind) => group.Kinds.Single(k => k.Kind == kind).Counts;

    // Invariants every plan's summary keeps: each resource counted exactly once, overall = space + projects, only Update / Fill carry
    // changes (and always some), a secret change never carries a value, and only not-imported resources carry a skip reason.
    private static void ShouldBeConsistent(OctopusImportScenario.ScenarioResult plan)
    {
        var summary = plan.Preview.Summary.ShouldNotBeNull();
        var resources = plan.Preview.Resources;

        summary.Counts.Total.ShouldBe(resources.Count);
        Sum(summary.Counts).ShouldBe(summary.Counts.Total);
        summary.Kinds.Sum(k => k.Counts.Total).ShouldBe(resources.Count);
        (summary.Space.Counts.Total + summary.Projects.Sum(p => p.Counts.Total)).ShouldBe(resources.Count);
        summary.Projects.ShouldAllBe(p => Sum(p.Counts) == p.Counts.Total);

        foreach (var resource in resources)
        {
            var writesIntoAnImportedRow = resource.PreviewAction is OctopusImportPreviewAction.Update or OctopusImportPreviewAction.FillMissingSecret;

            (resource.Changes.Count > 0).ShouldBe(writesIntoAnImportedRow, $"{resource.SourceType} {resource.SourceId} {resource.PreviewAction}");
            resource.Changes.Where(c => c.ValueHidden).ShouldAllBe(c => c.OldValue == null && c.NewValue == null);
            (resource.SkipReason != null).ShouldBe(resource.PreviewAction is OctopusImportPreviewAction.Skip or OctopusImportPreviewAction.Unsupported, $"{resource.SourceType} {resource.SourceId} {resource.PreviewAction}");
        }
    }

    private static int Sum(OctopusImportChangeCountsDto c) => c.AlreadyImported + c.Reused + c.Create + c.Update + c.Fill + c.Skip + c.Unsupported + c.Blocked;

    private static string Describe(OctopusImportScenario.ScenarioResult plan)
        => string.Join(System.Environment.NewLine, plan.Preview.Resources.Select(r => $"{r.SourceType} {r.SourceId} {r.PreviewAction} {string.Join(",", r.Diagnostics.Select(d => d.Code))}"));
}
