using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// A re-import only adds what is missing, through the REAL extraction → discovery → planner chain: an older copy of imported history is
/// never an update, an update names what it was previewed against, history left behind by a Squid delete moves onto its re-created owner
/// instead of being imported twice, and a channel Octopus added later is linked to the Squid channel of that name instead of duplicating it.
/// </summary>
public class OctopusImportNeverOverwritePreviewTests
{
    // ── Older copies ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Executing")]
    [InlineData("Queued")]
    public async Task TaskSquidHoldsFinished_SourceStillRunning_OlderCopy_KeptNotUpdated(string olderState)
    {
        var first = await History(state: "Success").PlanAsync();

        var plan = await History(state: olderState, finished: false).ImportedAs(first).PlanAsync();

        plan.Resource("Deployments-1").ShouldSatisfyAllConditions(
            r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported, Describe(plan)),
            r => r.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.OlderHistoryKept),
            r => r.PriorImportFingerprint.ShouldBeNull());
        plan.Resource("ServerTasks-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
    }

    [Fact]
    public async Task TaskStillRunningInSquid_SourceFurtherAlong_StillAnUpdate()
    {
        var first = await History(state: "Queued", finished: false).PlanAsync();

        var plan = await History(state: "Executing", finished: false).ImportedAs(first).PlanAsync();

        plan.Resource("Deployments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Update, Describe(plan));
    }

    // ── What an update was previewed against ───────────────────────────────

    [Fact]
    public async Task HistoryUpdates_NameTheRecordTheyWerePreviewedAgainst_OtherActionsNone()
    {
        var first = await History(notes: "v1", state: "Executing", finished: false).PlanAsync();
        var scenario = History(notes: "v2", state: "Success").ImportedAs(first);

        var plan = await scenario.PlanAsync();

        plan.Resource("Releases-1").PriorImportFingerprint.ShouldBe(OctopusImportPriorMappingPolicy.ImportBasis(new OctopusImportPriorMapping(0, "Created", OctopusImportFingerprint.ForRelease(new OctopusReleaseDto { ReleaseNotes = "v1" }), null)));
        plan.Resource("Deployments-1").PriorImportFingerprint.ShouldNotBeNull();
        plan.Preview.Resources.Where(r => r.PreviewAction != OctopusImportPreviewAction.Update).ShouldAllBe(r => r.PriorImportFingerprint == null);
    }

    // ── Owners deleted in Squid ────────────────────────────────────────────

    [Theory]
    [InlineData(OctopusResourceKind.Release, "Releases-1", "Deployments-1")]
    [InlineData(OctopusResourceKind.Environment, "Environments-1", "Deployments-1")]
    [InlineData(OctopusResourceKind.Channel, "Channels-1", "Releases-1,Deployments-1")]
    public async Task OwnerDeletedInSquid_ReCreated_HistoryStillInSquidMovesOntoIt(OctopusResourceKind ownerKind, string ownerId, string relinked)
    {
        var first = await History().PlanAsync();

        var scenario = History().ImportedAs(first);

        foreach (var id in relinked.Split(',')) scenario.OrphanedInSquid(id.StartsWith("Releases") ? OctopusResourceKind.Release : OctopusResourceKind.Deployment, id);

        var plan = await scenario.WithoutMapping(ownerKind, ownerId).PlanAsync();

        plan.Resource(ownerId).PreviewAction.ShouldBe(OctopusImportPreviewAction.Create, Describe(plan));

        foreach (var id in relinked.Split(','))
        {
            plan.Resource(id).ShouldSatisfyAllConditions(
                r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.Update, Describe(plan)),
                r => r.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.HistoryRelinked),
                r => r.Changes.Select(c => c.Field).ShouldBe([OctopusImportPreviewChanges.OwnerField]));
        }

        plan.Resource("ServerTasks-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Update);
        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
    }

    // The owner was re-created by an EARLIER import (an export file, or an API import of fewer projects that re-created a shared
    // environment): this plan sees it as already imported, yet the history a Squid delete orphaned must still move onto it.
    [Theory]
    [InlineData(OctopusResourceKind.Release, "Releases-1", "Deployments-1")]
    [InlineData(OctopusResourceKind.Environment, "Environments-1", "Deployments-1")]
    [InlineData(OctopusResourceKind.Channel, "Channels-1", "Releases-1,Deployments-1")]
    public async Task OwnerDeletedInSquid_ReCreatedByAnEarlierImport_HistoryStillInSquidMovesOntoIt(OctopusResourceKind ownerKind, string ownerId, string relinked)
    {
        var first = await History().PlanAsync();
        var scenario = History().ImportedAs(first);

        foreach (var id in relinked.Split(',')) scenario.OrphanedInSquid(id.StartsWith("Releases") ? OctopusResourceKind.Release : OctopusResourceKind.Deployment, id);

        var plan = await scenario.PlanAsync();

        plan.Resource(ownerId).PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported, Describe(plan));

        foreach (var id in relinked.Split(','))
        {
            plan.Resource(id).ShouldSatisfyAllConditions(
                r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.Update, Describe(plan)),
                r => r.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.HistoryRelinked),
                r => r.Changes.Select(c => c.Field).ShouldBe([OctopusImportPreviewChanges.OwnerField]));
        }

        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
    }

    [Fact]
    public async Task NothingDeleted_HistoryStaysAlreadyImported()
    {
        var first = await History().PlanAsync();

        var plan = await History().ImportedAs(first).PlanAsync();

        plan.Preview.Resources.Where(r => r.SourceType is "Release" or "Deployment" or "ServerTask").ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.AlreadyImported, Describe(plan));
    }

    // An owner re-created by this plan moves only the history a Squid delete left without it: a row whose owners all exist stays put.
    [Fact]
    public async Task OwnerReCreated_HistoryNotOrphaned_NotRelinked()
    {
        var first = await History().PlanAsync();

        var plan = await History().ImportedAs(first).WithoutMapping(OctopusResourceKind.Environment, "Environments-1").PlanAsync();

        plan.Resource("Environments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create, Describe(plan));
        plan.Resource("Deployments-1").ShouldSatisfyAllConditions(
            r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported, Describe(plan)),
            r => r.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.HistoryRelinked));
    }

    // Octopus moved an imported release onto a channel it added later: the release's Squid owners all exist, so it is never moved, and
    // its edited notes are a plain update (no owner change the preview would not show).
    [Fact]
    public async Task ReleaseMovedInOctopusOntoANewChannel_OwnersExist_NotesUpdatedReleaseStaysPut()
    {
        var first = await History(notes: "v1").PlanAsync();
        var scenario = History(notes: "v2", releaseChannel: "Channels-2").Channel("Channels-2", "Projects-1", "Hotfix").ImportedAs(first);

        var plan = await scenario.PlanAsync();

        plan.Resource("Channels-2").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create, Describe(plan));
        plan.Resource("Releases-1").ShouldSatisfyAllConditions(
            r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.Update, Describe(plan)),
            r => r.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.HistoryRelinked),
            r => r.Changes.Select(c => c.Field).ShouldNotContain(OctopusImportPreviewChanges.OwnerField));
    }

    // The orphaned release would move onto a channel linked by name that already holds its version: it is kept where it is.
    [Fact]
    public async Task OrphanedRelease_TargetChannelHoldsTheVersion_KeptNotRelinked()
    {
        var first = await History().PlanAsync();
        var scenario = History().ImportedAs(first).OrphanedInSquid(OctopusResourceKind.Release, "Releases-1").WithoutMapping(OctopusResourceKind.Channel, "Channels-1");
        var projectId = scenario.PriorDestinationOf(OctopusResourceKind.Project, "Projects-1");

        var plan = await scenario.ExistingChannel(77, projectId, "Main").ExistingRelease(projectId, 77, "1.0.0").PlanAsync();

        plan.Resource("Channels-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.ReuseExisting, Describe(plan));
        plan.Resource("Releases-1").ShouldSatisfyAllConditions(
            r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported, Describe(plan)),
            r => r.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ReleaseVersionExistsInSquid));
    }

    // ── Channels added in Octopus later ────────────────────────────────────

    [Fact]
    public async Task ChannelAddedInOctopus_TheProjectHasASquidChannelOfThatName_LinkedNotDuplicated()
    {
        var first = await History().PlanAsync();
        var scenario = WithHotfixChannel(History().ImportedAs(first));
        var projectId = scenario.PriorDestinationOf(OctopusResourceKind.Project, "Projects-1");

        var plan = await scenario.ExistingChannel(77, projectId, "Hotfix").PlanAsync();

        plan.Resource("Channels-2").ShouldSatisfyAllConditions(
            r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.ReuseExisting, Describe(plan)),
            r => r.DestinationId.ShouldBe(77));
        plan.Resource("Releases-2").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
    }

    [Fact]
    public async Task ChannelAddedInOctopus_SameNameOnlyInAnotherSquidProject_Created()
    {
        var first = await History().PlanAsync();
        var scenario = WithHotfixChannel(History().ImportedAs(first));

        var plan = await scenario.ExistingChannel(77, 9999, "Hotfix").PlanAsync();

        plan.Resource("Channels-2").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create, Describe(plan));
    }

    [Fact]
    public async Task ChannelAddedInOctopus_TwoSquidChannelsOfThatName_RenameRequired()
    {
        var first = await History().PlanAsync();
        var scenario = WithHotfixChannel(History().ImportedAs(first));
        var projectId = scenario.PriorDestinationOf(OctopusResourceKind.Project, "Projects-1");

        var plan = await scenario.ExistingChannel(77, projectId, "Hotfix").ExistingChannel(78, projectId, "hotfix").PlanAsync();

        plan.Resource("Channels-2").PreviewAction.ShouldBe(OctopusImportPreviewAction.RenameRequired, Describe(plan));
    }

    // Octopus renamed the imported channel and added a new one with its old name: the Squid channel the import made (still named the old
    // way) belongs to the first Octopus channel, so the new one is created, never merged onto it.
    [Fact]
    public async Task ChannelAddedInOctopus_SameNameAsASquidChannelLinkedToAnotherOctopusChannel_CreatedNotMerged()
    {
        var first = await History().PlanAsync();
        var scenario = History().ImportedAs(first).Channel("Channels-2", "Projects-1", "Main");
        var projectId = scenario.PriorDestinationOf(OctopusResourceKind.Project, "Projects-1");
        var importedChannel = scenario.PriorDestinationOf(OctopusResourceKind.Channel, "Channels-1");

        var plan = await scenario.ExistingChannel(importedChannel, projectId, "Main").PlanAsync();

        plan.Resource("Channels-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported, Describe(plan));
        plan.Resource("Channels-2").ShouldSatisfyAllConditions(
            r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.Create, Describe(plan)),
            r => r.DestinationId.ShouldBeNull());
    }

    [Fact]
    public async Task ChannelLinkedByName_AlreadyHoldsTheVersion_ReleaseSkippedNotDuplicated()
    {
        var first = await History().PlanAsync();
        var scenario = WithHotfixChannel(History().ImportedAs(first));
        var projectId = scenario.PriorDestinationOf(OctopusResourceKind.Project, "Projects-1");

        var plan = await scenario.ExistingChannel(77, projectId, "Hotfix").ExistingRelease(projectId, 77, "2.0.0").PlanAsync();

        plan.Resource("Releases-2").PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip, Describe(plan));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static OctopusImportScenario WithHotfixChannel(OctopusImportScenario scenario)
        => scenario.Channel("Channels-2", "Projects-1", "Hotfix").Release("Releases-2", "Projects-1", "Channels-2", "2.0.0");

    private static OctopusImportScenario History(string notes = "notes", string state = "Success", bool finished = true, string releaseChannel = "Channels-1")
    {
        var task = OctopusImportScenario.TaskDocument("ServerTasks-1", state);

        if (!finished) task.CompletedTime = null;

        return new OctopusImportScenario()
            .Environment("Environments-1", "Production")
            .Project("Projects-1", "Web", [], [OctopusImportScenario.Var("Variables-1", "Greeting", "hello")], OctopusImportScenario.ScriptAction("Actions-1", "echo hello"))
            .Channel("Channels-1", "Projects-1", "Main")
            .Release("Releases-1", "Projects-1", releaseChannel, "1.0.0", notes)
            .Deployment("Deployments-1", "Projects-1", "Channels-1", "Environments-1", "Releases-1", "ServerTasks-1")
            .ServerTaskDocument(task)
            .ExportedFromSpace(OctopusImportScenario.SuffixedExportSpaceId);
    }

    private static string Describe(OctopusImportScenario.ScenarioResult plan)
        => string.Join(System.Environment.NewLine, plan.Preview.Resources.Select(r => $"{r.SourceType} {r.SourceId} {r.PreviewAction} {string.Join(",", r.Diagnostics.Select(d => d.Code))}"));
}
