using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// The incremental preview against the real database, for both sources: every repeat import says "up to date" with no field differences
/// and names the Squid project (id and current name) the project's changes land on; history changed in Octopus previews as Update with
/// Squid's stored value on the old side; nothing a user edited in Squid is overwritten.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    private const string RenamedInSquid = "Renamed in Squid";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatImports_EitherOrder_PreviewUpToDate_OnTheSquidProject_NoChanges_UserEditsKept(bool serverFirst)
    {
        await using var octopus = OneDeployment();

        var first = serverFirst ? await ImportFromServerAsync(octopus) : await ImportFromFileAsync(ExportArchive(withHistory: true));

        ShouldHaveSucceeded(first);
        first.PreviewPlan.Summary.IsUpToDate.ShouldBeFalse();
        first.PreviewPlan.Summary.Projects.Single().Destination.State.ShouldBe(OctopusImportProjectDestinationState.New);

        if (!serverFirst) await ShouldPreviewTheDetailsEnrichmentAsUpdateAsync(octopus);

        var project = await SingleAsync<Project>();
        await RenameProjectAsync(project.Id, RenamedInSquid);
        var before = await CountRowsAsync();

        foreach (var fromServer in new[] { true, false, true })
        {
            var again = fromServer ? await ImportFromServerAsync(octopus) : await ImportFromFileAsync(ExportArchive(withHistory: true));
            var source = fromServer ? "API" : "file";

            ShouldHaveSucceeded(again);
            var summary = again.PreviewPlan.Summary;
            summary.IsUpToDate.ShouldBeTrue($"repeat {source} import: {Describe(again.PreviewPlan)}");
            summary.Counts.Incremental.ShouldBe(0);
            summary.Projects.Single().Destination.ShouldSatisfyAllConditions(d => d.State.ShouldBe(OctopusImportProjectDestinationState.Existing), d => d.ProjectId.ShouldBe(project.Id), d => d.ProjectName.ShouldBe(RenamedInSquid));
            again.PreviewPlan.Resources.ShouldAllBe(r => r.Changes.Count == 0, $"repeat {source} import");
            (await CountRowsAsync()).ShouldBe(before, $"a repeat {source} import writes nothing");
            (await SingleAsync<Project>()).Name.ShouldBe(RenamedInSquid, "a name changed in Squid is never overwritten");
        }
    }

    // History changed in Octopus: the preview shows Squid's stored value → Octopus's value, field by field, then one import applies it.
    [Fact]
    public async Task HistoryChangedInOctopus_PreviewShowsSquidValueToOctopusValue_ThenUpToDate()
    {
        await using var octopus = OneDeployment();
        SetTaskExecuting(octopus);
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var project = await SingleAsync<Project>();

        octopus.Tasks[TaskId]["State"] = "Success";
        octopus.Tasks[TaskId]["CompletedTime"] = "2024-01-02T03:05:06Z";
        octopus.Releases["Projects-1"][0]["ReleaseNotes"] = "notes edited in Octopus";
        var changed = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(changed);
        var plan = changed.PreviewPlan;
        Resource(plan, "Release").Changes.Select(c => (c.Field, c.OldValue, c.NewValue)).ShouldBe([(OctopusImportPreviewChanges.ReleaseNotesField, "notes 1.0.0", "notes edited in Octopus")]);
        var deployment = Resource(plan, "Deployment");
        deployment.Changes.ShouldContain(c => c.Field == OctopusImportPreviewChanges.StateField && c.OldValue == "Executing" && c.NewValue == "Success", Describe(deployment));
        deployment.Changes.ShouldContain(c => c.Field == OctopusImportPreviewChanges.CompletedTimeField && c.OldValue == null && c.NewValue == "2024-01-02T03:05:06.000Z", Describe(deployment));
        Resource(plan, "ServerTask").Changes.Select(c => c.Field).ShouldBe(deployment.Changes.Select(c => c.Field));
        plan.Summary.Counts.Update.ShouldBe(3);
        plan.Summary.IsUpToDate.ShouldBeFalse();
        plan.Summary.Projects.Single().Destination.ProjectId.ShouldBe(project.Id);
        (await SingleAsync<Release>()).ReleaseNote.ShouldBe("notes edited in Octopus");

        var again = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(again);
        again.PreviewPlan.Summary.IsUpToDate.ShouldBeTrue(Describe(again.PreviewPlan));
    }

    // Notes edited in Squid, then changed in Octopus: neither source overwrites the user's notes; the release is skipped with the reason,
    // the deployment still lands on it, and the preview stays up to date.
    [Fact]
    public async Task ReleaseNotesEditedInSquid_ThenChangedInOctopus_NeitherSourceOverwritesThem()
    {
        await using var octopus = OneDeployment();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        await Run<SquidDbContext>(db => db.Set<Release>().ExecuteUpdateAsync(s => s.SetProperty(r => r.ReleaseNote, "notes 1.0.0 + approved by ops")));

        octopus.Releases["Projects-1"][0]["ReleaseNotes"] = "notes edited in Octopus";

        foreach (var fromServer in new[] { true, false, true })
        {
            var again = fromServer ? await ImportFromServerAsync(octopus) : await ImportFromFileAsync(ExportArchive(withHistory: true));

            ShouldHaveSucceeded(again);
            (await SingleAsync<Release>()).ReleaseNote.ShouldBe("notes 1.0.0 + approved by ops", $"{(fromServer ? "API" : "file")} import");
            again.PreviewPlan.Summary.Counts.Update.ShouldBe(0, Describe(again.PreviewPlan));
            again.PreviewPlan.Summary.IsUpToDate.ShouldBeTrue(Describe(again.PreviewPlan));
        }

        var release = Resource((await ImportFromServerAsync(octopus)).PreviewPlan, "Release");
        release.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        release.SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ReleaseNotesEditedInSquid);
    }

    // The confirmation's write is a compare-and-set on the notes it read: an edit that lands in between wins.
    [Fact]
    public async Task ReleaseNotesCompareAndSet_WritesOnlyWhileSquidStillHoldsTheReadNotes()
    {
        await using var octopus = OneDeployment();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var release = await SingleAsync<Release>();

        var lost = await Run<IOctopusImportDeploymentHistoryImporter, bool>(importer => importer.UpdateReleaseNotesIfUnchangedAsync(release.Id, "notes read before an edit", "from Octopus"));
        (await SingleAsync<Release>()).ReleaseNote.ShouldBe("notes 1.0.0");

        var won = await Run<IOctopusImportDeploymentHistoryImporter, bool>(importer => importer.UpdateReleaseNotesIfUnchangedAsync(release.Id, "notes 1.0.0", "from Octopus"));

        (lost, won).ShouldBe((false, true));
        (await SingleAsync<Release>()).ReleaseNote.ShouldBe("from Octopus");
    }

    // The old side is read only from the destination space.
    [Fact]
    public async Task StoredHistory_ReadFromTheDestinationSpaceOnly()
    {
        await using var octopus = OneDeployment();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var release = await SingleAsync<Release>();
        var task = await SingleAsync<ServerTaskEntity>();

        var stored = await Run<IOctopusImportDestinationDataProvider, OctopusImportStoredHistory>(p => p.GetStoredHistoryAsync(SpaceId, [release.Id], [task.Id]));
        var otherSpace = await Run<IOctopusImportDestinationDataProvider, OctopusImportStoredHistory>(p => p.GetStoredHistoryAsync(SpaceId + 1, [release.Id], [task.Id]));

        stored.ReleaseNotes.ShouldBe(new Dictionary<int, string> { [release.Id] = "notes 1.0.0" });
        stored.Tasks[task.Id].ShouldSatisfyAllConditions(t => t.State.ShouldBe(task.State), t => t.QueueTime.ShouldBe(task.QueueTime), t => t.CompletedTime.ShouldBe(task.CompletedTime), t => t.HasWarningsOrErrors.ShouldBe(task.HasWarningsOrErrors));
        otherSpace.ReleaseNotes.ShouldBeEmpty();
        otherSpace.Tasks.ShouldBeEmpty();
    }

    // An export file carries no task details; the API adds them to the history the file created: an Update that names the details.
    private async Task ShouldPreviewTheDetailsEnrichmentAsUpdateAsync(StubOctopusServer octopus)
    {
        var enriched = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(enriched);
        var deployment = Resource(enriched.PreviewPlan, "Deployment");
        deployment.PreviewAction.ShouldBe(OctopusImportPreviewAction.Update, Describe(enriched.PreviewPlan));
        deployment.Changes.Select(c => c.Field).ShouldBe([OctopusImportPreviewChanges.DetailsField], Describe(deployment));
        enriched.PreviewPlan.Summary.IsUpToDate.ShouldBeFalse();
        enriched.PreviewPlan.Summary.Projects.Single().Destination.State.ShouldBe(OctopusImportProjectDestinationState.Existing);
    }

    private Task RenameProjectAsync(int projectId, string name)
        => Run<SquidDbContext>(db => db.Set<Project>().Where(p => p.Id == projectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, name)));

    private static OctopusImportResourceResultDto Resource(OctopusImportPreviewPlanDto plan, string sourceType) => plan.Resources.Single(r => r.SourceType == sourceType);

    private static string Describe(OctopusImportResourceResultDto resource)
        => string.Join("; ", resource.Changes.Select(c => $"{c.Field}: {c.OldValue} -> {c.NewValue} ({c.Description})"));
}
