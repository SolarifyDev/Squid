using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Persistence.Entities.Events;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Constants;
using Squid.Message.Enums.Deployments;
using Squid.Message.Enums.Events;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Re-importing Octopus history through the real planning pipeline and confirmation orchestrator against a real
/// database: the persistent source map makes every re-import insert only what is missing and update only what changed.
/// </summary>
public class OctopusImportIdempotencyIntegrationTests : TestBase, IAsyncLifetime
{
    private const int SpaceId = 1;
    private const string InstallationId = "6c9b6a52-1d7e-4f4b-9a54-6f0a2b9e3c11";

    private static readonly string[] CountedTables =
    [
        "project_group", "environment", "lifecycle", "phase", "project", "channel", "variable_set", "deployment_process", "release",
        "deployment", "server_task", "deployment_completion", "activity_log", "server_task_log", "deployment_interruption", "event", "octopus_import_source_map"
    ];

    private readonly List<string> _paths = [];

    public OctopusImportIdempotencyIntegrationTests()
        : base("OctopusImportIdempotency", "squid_it_octopus_import_idempotency")
    {
    }

    [Fact]
    public async Task ImportSameArchiveTwice_RowCountsIdentical_AllTables()
    {
        var archive = Archive(new HistorySpec());

        await ConfirmSucceededAsync(archive);
        var before = await CountRowsAsync();

        var (snapshot, result) = await PreviewAndConfirmAsync(archive);

        snapshot.PreviewPlan.Resources.ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.AlreadyImported || r.PreviewAction == OctopusImportPreviewAction.Skip, Describe(snapshot.PreviewPlan));
        snapshot.PreviewPlan.Resources.ShouldContain(r => r.SourceType == "Deployment" && r.PreviewAction == OctopusImportPreviewAction.AlreadyImported);
        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        (await CountRowsAsync()).ShouldBe(before);
        before["deployment"].ShouldBe(1);
        before["activity_log"].ShouldBe(1);
        (await SourceMapAsync()).ShouldContain(m => m.SourceType == OctopusImportSourceMapQueries.InstallationSourceType && m.SourceId == InstallationId.ToUpperInvariant() && m.DestinationId == SpaceId);
    }

    [Fact]
    public async Task ChangedReleaseNotes_UpdatesOnlyThatRow()
    {
        await ConfirmSucceededAsync(Archive(new HistorySpec { ReleaseNotes = "First notes" }));
        var release = await SingleAsync<Release>();
        var before = await CountRowsAsync();

        var (snapshot, result) = await PreviewAndConfirmAsync(Archive(new HistorySpec { ReleaseNotes = "Hotfix notes" }));

        snapshot.PreviewPlan.Resources.Single(r => r.SourceType == "Release").PreviewAction.ShouldBe(OctopusImportPreviewAction.Update);
        snapshot.PreviewPlan.Resources.Single(r => r.SourceType == "Deployment").PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        (await CountRowsAsync()).ShouldBe(before);
        var updated = await SingleAsync<Release>();
        updated.Id.ShouldBe(release.Id);
        updated.ReleaseNote.ShouldBe("Hotfix notes");
        updated.Version.ShouldBe(release.Version);

        (await PreviewAsync(Archive(new HistorySpec { ReleaseNotes = "Hotfix notes" }))).PreviewPlan.Resources.Single(r => r.SourceType == "Release").PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
    }

    [Fact]
    public async Task ExecutingThenSuccess_UpdatesTask_AddsCompletion_NoDuplicates()
    {
        await ConfirmSucceededAsync(Archive(new HistorySpec { State = "Executing", CompletedTime = null }));
        var task = await SingleAsync<ServerTaskEntity>();
        (await CountAsync<DeploymentCompletion>()).ShouldBe(0);

        var (snapshot, result) = await PreviewAndConfirmAsync(Archive(new HistorySpec()));

        snapshot.PreviewPlan.Resources.Where(r => r.SourceType is "Deployment" or "ServerTask").ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.Update);
        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        var updated = await SingleAsync<ServerTaskEntity>();
        updated.Id.ShouldBe(task.Id);
        updated.State.ShouldBe(TaskState.Success);
        updated.BusinessProcessState.ShouldBe("Completed");
        updated.StateOrder.ShouldBe(30);
        (await SingleAsync<DeploymentCompletion>()).State.ShouldBe(TaskState.Success);
        (await CountAsync<Deployment>()).ShouldBe(1);
        (await CountAsync<ActivityLog>()).ShouldBe(1);
        (await EventsAsync()).ShouldHaveSingleItem().Category.ShouldBe(EventCategory.DeploymentSucceeded);
    }

    [Fact]
    public async Task DetailsNeverDowngraded_ByFileReimport()
    {
        var sessionDirectory = SessionDirectory();
        var details = Details();
        await StoreDetailsAsync(sessionDirectory, details);
        var detailFingerprint = OctopusImportFingerprint.ForTaskDetails(details);

        await ConfirmSucceededAsync(Archive(new HistorySpec { DetailFingerprint = detailFingerprint }), sessionDirectory);
        var detailedNodes = await CountAsync<ActivityLog>();
        detailedNodes.ShouldBe(3);
        (await CountAsync<DeploymentInterruption>()).ShouldBe(1);
        (await SingleAsync<ServerTaskEntity>()).JSON.ShouldContain("deploy.log");

        var (snapshot, result) = await PreviewAndConfirmAsync(Archive(new HistorySpec { ErrorMessage = "warning only" }));

        snapshot.PreviewPlan.Resources.Single(r => r.SourceType == "Deployment").PreviewAction.ShouldBe(OctopusImportPreviewAction.Update);
        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        (await SingleAsync<ServerTaskEntity>()).ErrorMessage.ShouldBe("warning only");
        (await CountAsync<ActivityLog>()).ShouldBe(detailedNodes);
        (await CountAsync<DeploymentInterruption>()).ShouldBe(1);
        (await SingleAsync<ServerTaskEntity>()).JSON.ShouldContain("deploy.log");
        (await SourceMapAsync()).Single(m => m.SourceType == "Deployment").DetailFingerprint.ShouldBe(detailFingerprint);
    }

    [Fact]
    public async Task DetailsOnlyChange_ReplacesChildren_KeepsTheTaskAndCompletionRows()
    {
        await ConfirmSucceededAsync(Archive(new HistorySpec()));
        var task = await SingleAsync<ServerTaskEntity>();
        var completion = await SingleAsync<DeploymentCompletion>();
        var sessionDirectory = SessionDirectory();
        var details = Details();
        await StoreDetailsAsync(sessionDirectory, details);

        var (snapshot, result) = await PreviewAndConfirmAsync(Archive(new HistorySpec { DetailFingerprint = OctopusImportFingerprint.ForTaskDetails(details) }), sessionDirectory);

        snapshot.PreviewPlan.Resources.Single(r => r.SourceType == "Deployment").PreviewAction.ShouldBe(OctopusImportPreviewAction.Update);
        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        (await CountAsync<ActivityLog>()).ShouldBe(3);
        (await SingleAsync<ServerTaskEntity>()).Id.ShouldBe(task.Id);
        // The task did not change, so its completion (and its place in the completion order) is kept as it was.
        (await SingleAsync<DeploymentCompletion>()).SequenceNumber.ShouldBe(completion.SequenceNumber);
    }

    [Fact]
    public async Task DetailLevel_WritesTreeLogsInterruptionsAndOctopusEvents()
    {
        var sessionDirectory = SessionDirectory();
        var details = Details();
        await StoreDetailsAsync(sessionDirectory, details);

        await ConfirmSucceededAsync(Archive(new HistorySpec { DetailFingerprint = OctopusImportFingerprint.ForTaskDetails(details) }), sessionDirectory);

        var nodes = await ListAsync<ActivityLog>();
        nodes.Single(n => n.ParentId == null).NodeType.ShouldBe(DeploymentActivityLogNodeType.Task);
        nodes.Count(n => n.ParentId != null).ShouldBe(2);
        var logs = await ListAsync<ServerTaskLog>();
        logs.Select(l => l.MessageText).ShouldBe(["Deploying", "Step started", "Step failed once"], ignoreOrder: true);
        logs.ShouldAllBe(l => l.ActivityNodeId != null);
        var interruption = await SingleAsync<DeploymentInterruption>();
        interruption.Resolution.ShouldBe("Proceed");
        interruption.ResolvedAt.ShouldNotBeNull();
        var events = await EventsAsync();
        events.Select(e => e.Category).ShouldBe([EventCategory.ManualInterventionSubmitted, EventCategory.ImportedOctopusEvent, EventCategory.DeploymentSucceeded], ignoreOrder: true);
        events.Single(e => e.Category == EventCategory.ImportedOctopusEvent).ReferencesJson.ShouldContain("CustomOctopusCategory");
        (await SingleAsync<ServerTaskEntity>()).HasPendingInterruptions.ShouldBeFalse();
    }

    [Fact]
    public async Task BackfilledHistory_ReimportNormalizes_NoDuplicateEvents()
    {
        var archive = Archive(new HistorySpec());
        await ConfirmSucceededAsync(archive);
        await SimulateBackfilledPreviousImportAsync();

        var (snapshot, result) = await PreviewAndConfirmAsync(archive);

        snapshot.PreviewPlan.Resources.Single(r => r.SourceType == "Deployment").PreviewAction.ShouldBe(OctopusImportPreviewAction.Update);
        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        (await EventsAsync()).ShouldHaveSingleItem().Category.ShouldBe(EventCategory.DeploymentSucceeded);
        (await ListAsync<ActivityLog>()).ShouldHaveSingleItem().ParentId.ShouldBeNull();
        (await ListAsync<ServerTaskLog>()).ShouldHaveSingleItem().ActivityNodeId.ShouldNotBeNull();
        (await CountAsync<DeploymentCompletion>()).ShouldBe(1);
        (await SourceMapAsync()).Single(m => m.SourceType == "Deployment").Fingerprint.ShouldNotBeNull();

        (await PreviewAsync(archive)).PreviewPlan.Resources.Single(r => r.SourceType == "Deployment").PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
    }

    [Fact]
    public async Task StaleMapRow_SelfHealsCreate()
    {
        var archive = Archive(new HistorySpec());
        await ConfirmSucceededAsync(archive);
        var deleted = await SingleAsync<Deployment>();
        await DeleteDeploymentHistoryAsync(deleted);

        var (snapshot, result) = await PreviewAndConfirmAsync(archive);

        snapshot.PreviewPlan.Resources.Where(r => r.SourceType is "Deployment" or "ServerTask").ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.Create);
        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        var recreated = await SingleAsync<Deployment>();
        recreated.Id.ShouldNotBe(deleted.Id);
        (await SourceMapAsync()).Single(m => m.SourceType == "Deployment").DestinationId.ShouldBe(recreated.Id);
        (await SourceMapAsync()).Single(m => m.SourceType == "ServerTask").DestinationId.ShouldBe(recreated.TaskId ?? 0);
    }

    [Fact]
    public async Task TaskRowGone_RecreatesTheTaskUnderTheSameDeployment()
    {
        var archive = Archive(new HistorySpec());
        await ConfirmSucceededAsync(archive);
        var deployment = await SingleAsync<Deployment>();
        await DeleteTaskAsync(deployment.TaskId ?? 0);

        var (snapshot, result) = await PreviewAndConfirmAsync(archive);

        snapshot.PreviewPlan.Resources.Single(r => r.SourceType == "Deployment").PreviewAction.ShouldBe(OctopusImportPreviewAction.Update);
        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        var task = await SingleAsync<ServerTaskEntity>();
        var updated = await SingleAsync<Deployment>();
        updated.Id.ShouldBe(deployment.Id);
        updated.TaskId.ShouldBe(task.Id);
        task.Id.ShouldNotBe(deployment.TaskId ?? 0);
        (await CountAsync<ActivityLog>()).ShouldBe(1);
        (await SourceMapAsync()).Single(m => m.SourceType == "ServerTask").DestinationId.ShouldBe(task.Id);
    }

    [Fact]
    public async Task TaskRowGone_ReimportWithoutDetails_ClearsTheStoredDetailFingerprint()
    {
        var sessionDirectory = SessionDirectory();
        var details = Details();
        await StoreDetailsAsync(sessionDirectory, details);
        await ConfirmSucceededAsync(Archive(new HistorySpec { DetailFingerprint = OctopusImportFingerprint.ForTaskDetails(details) }), sessionDirectory);
        await DeleteTaskAsync((await SingleAsync<Deployment>()).TaskId ?? 0);

        await ConfirmSucceededAsync(Archive(new HistorySpec()));

        var stored = (await SourceMapAsync()).Single(m => m.SourceType == "Deployment");
        stored.DetailFingerprint.ShouldBeNull("the re-created task holds no details, so a later REST fetch must fetch them again");
        stored.Fingerprint.ShouldNotBeNull();
        (await CountAsync<ActivityLog>()).ShouldBe(1);
    }

    // Octopus moved the project's default to a channel created after the first import: the project keeps its default
    // channel, and the new one is created as a regular channel so its releases and deployments still arrive.
    [Fact]
    public async Task NewOctopusDefaultChannel_UnderImportedProject_IsCreatedAsRegularChannelWithItsHistory()
    {
        await ConfirmSucceededAsync(Archive(new HistorySpec()));
        var original = await SingleAsync<Channel>();

        var (snapshot, result) = await PreviewAndConfirmAsync(Archive(new HistorySpec { MovedDefaultChannel = true }));

        snapshot.PreviewPlan.Resources.Where(r => r.SourceId is "Channels-9" or "Releases-2" or "Deployments-2" or "ServerTasks-2").ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.Create, Describe(snapshot.PreviewPlan));
        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        var channels = await ListAsync<Channel>();
        channels.Count.ShouldBe(2);
        channels.Single(c => c.IsDefault).Id.ShouldBe(original.Id);
        var added = channels.Single(c => c.Id != original.Id);
        added.Name.ShouldBe("Hotfix");
        var release = (await ListAsync<Release>()).Single(r => r.Version == "2.0.0");
        release.ChannelId.ShouldBe(added.Id);
        (await ListAsync<Deployment>()).Where(d => d.ReleaseId == release.Id).ShouldHaveSingleItem();
    }

    // Squid deletes never cascade, so the deleted project's channel, variable set, process, releases and history rows
    // survive as orphans: their map rows must not count as "already imported" under the re-created project.
    [Fact]
    public async Task DeletedProject_ReimportRecreatesTheWholeProject()
    {
        var archive = Archive(new HistorySpec { WithConfiguration = true });
        await ConfirmSucceededAsync(archive);
        var deleted = await SingleAsync<Project>();
        await Run<IProjectDataProvider>(provider => provider.DeleteProjectsAsync([deleted]));

        var (snapshot, result) = await PreviewAndConfirmAsync(archive);

        snapshot.PreviewPlan.Resources.Where(r => r.SourceType is not ("ProjectGroup" or "Environment" or "Lifecycle" or "LifecyclePhase")).ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.Create, Describe(snapshot.PreviewPlan));
        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        var project = await SingleAsync<Project>();
        project.Id.ShouldNotBe(deleted.Id);
        (await ListAsync<Variable>()).Where(v => v.VariableSetId == project.VariableSetId).Select(v => v.Name).ShouldBe(["Imported variable"]);
        (await ListAsync<DeploymentStep>()).Where(s => s.ProcessId == project.DeploymentProcessId).Select(s => s.Name).ShouldBe(["Imported step"]);
        var release = (await ListAsync<Release>()).Where(r => r.ProjectId == project.Id).ShouldHaveSingleItem();
        (await ListAsync<Channel>()).Where(c => c.ProjectId == project.Id).Select(c => c.Id).ShouldContain(release.ChannelId);
        var deployment = (await ListAsync<Deployment>()).Where(d => d.ProjectId == project.Id).ShouldHaveSingleItem();
        deployment.ReleaseId.ShouldBe(release.Id);
        (await SourceMapAsync()).Single(m => m.SourceType == "Release").DestinationId.ShouldBe(release.Id);
        (await PreviewAsync(archive)).PreviewPlan.Resources.Where(r => r.SourceType is "Variable" or "DeploymentStep" or "Release" or "Deployment").ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.AlreadyImported);
    }

    [Fact]
    public async Task DeletedRelease_ReimportRecreatesTheReleaseWithItsHistory()
    {
        var archive = Archive(new HistorySpec());
        await ConfirmSucceededAsync(archive);
        var deleted = await SingleAsync<Release>();
        await Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync("DELETE FROM release WHERE id = {0}", deleted.Id));

        var (snapshot, result) = await PreviewAndConfirmAsync(archive);

        snapshot.PreviewPlan.Resources.Where(r => r.SourceType is "Release" or "Deployment" or "ServerTask").ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.Create, Describe(snapshot.PreviewPlan));
        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        var release = await SingleAsync<Release>();
        release.Id.ShouldNotBe(deleted.Id);
        (await ListAsync<Deployment>()).Where(d => d.ReleaseId == release.Id).ShouldHaveSingleItem();
        (await SourceMapAsync()).Single(m => m.SourceType == "Deployment").DestinationId.ShouldBe((await ListAsync<Deployment>()).Single(d => d.ReleaseId == release.Id).Id);
    }

    [Fact]
    public async Task ConcurrentConfirms_SecondFailsStale_NoDuplicates()
    {
        var first = await PreviewAsync(Archive(new HistorySpec()));
        var second = await PreviewAsync(Archive(new HistorySpec()));
        var sessions = new[] { await SeedValidatedSessionAsync(), await SeedValidatedSessionAsync() };

        var results = await Task.WhenAll(ConfirmAsync(sessions[0], first), ConfirmAsync(sessions[1], second));

        results.Count(r => r.State == OctopusImportSessionState.Succeeded).ShouldBe(1, string.Join(" | ", results.Select(r => Describe(r.Result))));
        var failed = results.Single(r => r.State == OctopusImportSessionState.Failed);
        failed.Result.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan, Describe(failed.Result));
        (await CountAsync<Project>()).ShouldBe(1);
        (await CountAsync<Release>()).ShouldBe(1);
        (await CountAsync<Deployment>()).ShouldBe(1);
        (await CountAsync<ServerTaskEntity>()).ShouldBe(1);
    }

    [Fact]
    public async Task LongTaskName_IsTruncatedToTheColumn_FullNameAndRawMachinesKeptInJson()
    {
        var longName = new string('x', 80);

        await ConfirmSucceededAsync(Archive(new HistorySpec { TaskName = longName }));

        var task = await SingleAsync<ServerTaskEntity>();
        task.Name.ShouldBe(longName[..50]);
        task.JSON.ShouldContain(longName);
        var deployment = await SingleAsync<Deployment>();
        deployment.DeployedToMachineIds.ShouldBe("[\"Machines-1\"]");
        deployment.Json.ShouldContain("\"Machines\":[{\"Id\":\"Machines-1\"");
    }

    [Fact]
    public async Task FailedDeployment_WritesFailedCompletion()
    {
        await ConfirmSucceededAsync(Archive(new HistorySpec { State = "Failed", ErrorMessage = "Step failed" }));

        (await SingleAsync<DeploymentCompletion>()).State.ShouldBe(TaskState.Failed);
        (await SingleAsync<ServerTaskEntity>()).BusinessProcessState.ShouldBe("Completed");
        (await EventsAsync()).ShouldHaveSingleItem().Category.ShouldBe(EventCategory.DeploymentFailed);
    }

    [Fact]
    public async Task NonTerminalTask_ImportedVerbatim_NotInConcurrencyIndex()
    {
        await ConfirmSucceededAsync(Archive(new HistorySpec { State = "Executing", CompletedTime = null }));

        var task = await SingleAsync<ServerTaskEntity>();
        task.State.ShouldBe(TaskState.Executing);
        task.ConcurrencyTag.ShouldBeNull();
        task.CompletedTime.ShouldBeNull();
        task.BusinessProcessState.ShouldBe("InProgress");
        task.StateOrder.ShouldBe(9);
        task.HasPendingInterruptions.ShouldBeFalse();
        (await CountAsync<DeploymentCompletion>()).ShouldBe(0);
        (await EventsAsync()).ShouldBeEmpty();
        (await SingleAsync<ActivityLog>()).Status.ShouldBe(DeploymentActivityLogNodeStatus.Running);
    }

    // Temporary archives and session directories are removed even when an assertion failed.
    Task IAsyncLifetime.DisposeAsync()
    {
        foreach (var path in _paths)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }

        return Task.CompletedTask;
    }

    // ── Flow ───────────────────────────────────────────────────────────────

    private async Task ConfirmSucceededAsync(string archivePath, string sessionDirectory = null)
    {
        var (_, result) = await PreviewAndConfirmAsync(archivePath, sessionDirectory);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
    }

    private async Task<(OctopusImportPlanningSnapshot Snapshot, OctopusImportSessionDto Result)> PreviewAndConfirmAsync(string archivePath, string sessionDirectory = null)
    {
        var snapshot = await PreviewAsync(archivePath);
        var sessionId = await SeedValidatedSessionAsync();

        snapshot.PreviewPlan.HasBlockers.ShouldBeFalse(Describe(snapshot.PreviewPlan));

        return (snapshot, await ConfirmAsync(sessionId, snapshot, sessionDirectory));
    }

    private Task<OctopusImportPlanningSnapshot> PreviewAsync(string archivePath)
        => Run<IOctopusImportPlanningPipeline, OctopusImportPlanningSnapshot>(pipeline => pipeline.BuildPreviewAsync(archivePath, SpaceId));

    private Task<OctopusImportSessionDto> ConfirmAsync(Guid sessionId, OctopusImportPlanningSnapshot snapshot, string sessionDirectory = null)
        => Run<IOctopusImportConfirmationOrchestrator, OctopusImportSessionDto>(orchestrator => orchestrator.ConfirmAsync(new OctopusImportConfirmationRequest(sessionId, SpaceId, snapshot.Graph, snapshot.DependencyPlan, snapshot.PreviewPlan) { SessionDirectory = sessionDirectory }));

    private async Task<Guid> SeedValidatedSessionAsync()
    {
        var sessionId = Guid.NewGuid();

        await Run<SquidDbContext>(async db =>
        {
            db.Set<OctopusImportSession>().Add(new OctopusImportSession { SessionId = sessionId, DestinationSpaceId = SpaceId, OwnerUserId = CurrentUsers.InternalUser.Id, State = OctopusImportSessionState.Validated.ToString(), SourceSummaryJson = "{}", DataVersion = Guid.NewGuid().ToByteArray(), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), LastStateChangedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });

        return sessionId;
    }

    // What #542 left behind, as the backfill records it: map rows without fingerprints, and history without an activity tree.
    private Task SimulateBackfilledPreviousImportAsync()
        => Run<SquidDbContext>(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE octopus_import_source_map SET fingerprint = NULL, detail_fingerprint = NULL");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM activity_log");
            await db.Database.ExecuteSqlRawAsync("UPDATE server_task_log SET activity_node_id = NULL");
        });

    private Task DeleteDeploymentHistoryAsync(Deployment deployment)
        => Run<SquidDbContext>(async db =>
        {
            foreach (var table in new[] { "activity_log", "server_task_log", "deployment_interruption", "event" })
                await db.Database.ExecuteSqlRawAsync($"DELETE FROM {table} WHERE server_task_id = {{0}}", deployment.TaskId);

            await db.Database.ExecuteSqlRawAsync("DELETE FROM deployment_completion WHERE deployment_id = {0}", deployment.Id);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM deployment WHERE id = {0}", deployment.Id);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM server_task WHERE id = {0}", deployment.TaskId);
        });

    private Task DeleteTaskAsync(int taskId)
        => Run<SquidDbContext>(async db =>
        {
            foreach (var table in new[] { "activity_log", "server_task_log", "deployment_interruption", "event" })
                await db.Database.ExecuteSqlRawAsync($"DELETE FROM {table} WHERE server_task_id = {{0}}", taskId);

            await db.Database.ExecuteSqlRawAsync("DELETE FROM server_task WHERE id = {0}", taskId);
        });

    // ── Queries ────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, int>> CountRowsAsync()
    {
        var counts = new Dictionary<string, int>();

        await Run<SquidDbContext>(async db =>
        {
            foreach (var table in CountedTables)
                counts[table] = await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table}").SingleAsync();
        });

        return counts;
    }

    private Task<List<T>> ListAsync<T>() where T : class
        => Run<SquidDbContext, List<T>>(db => db.Set<T>().AsNoTracking().ToListAsync());

    private Task<T> SingleAsync<T>() where T : class
        => Run<SquidDbContext, T>(db => db.Set<T>().AsNoTracking().SingleAsync());

    private Task<int> CountAsync<T>() where T : class
        => Run<SquidDbContext, int>(db => db.Set<T>().CountAsync());

    private Task<List<Event>> EventsAsync()
        => Run<SquidDbContext, List<Event>>(db => db.Set<Event>().AsNoTracking().Where(e => e.ServerTaskId != null).ToListAsync());

    private Task<List<OctopusImportSourceMap>> SourceMapAsync()
        => Run<SquidDbContext, List<OctopusImportSourceMap>>(db => db.Set<OctopusImportSourceMap>().AsNoTracking().ToListAsync());

    private static string Describe(OctopusImportPreviewPlanDto plan)
        => string.Join(System.Environment.NewLine, plan.Resources.Select(r => $"{r.SourceType} {r.SourceId} {r.PreviewAction} {string.Join(",", r.Diagnostics.Select(d => d.Code))}").Concat(plan.Diagnostics.Select(d => $"{d.Severity} {d.Code} {d.Message}")));

    private static string Describe(OctopusImportSessionResultDto result)
        => result == null ? "no result" : string.Join(System.Environment.NewLine, result.Diagnostics.Concat(result.Resources.SelectMany(r => r.Diagnostics)).Select(d => $"{d.Severity} {d.Code} [{d.SourceId}] {d.Message}"));

    // ── Archive ────────────────────────────────────────────────────────────

    private sealed record HistorySpec
    {
        public string ReleaseNotes { get; init; } = "Initial notes";
        public string State { get; init; } = "Success";
        public string CompletedTime { get; init; } = "2024-07-10T09:48:11+00:00";
        public string ErrorMessage { get; init; } = "";
        public string DetailFingerprint { get; init; }
        public string TaskName { get; init; } = "Deploy";
        public bool WithConfiguration { get; init; }
        public bool MovedDefaultChannel { get; init; }
    }

    private string SessionDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"squid-octopus-idempotency-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);
        _paths.Add(directory);

        return directory;
    }

    private Task StoreDetailsAsync(string sessionDirectory, OctopusTaskHistoryDetails details)
        => Run<IOctopusImportHistoryDetailsStore>(store => store.WriteAsync(sessionDirectory, "ServerTasks-1", details));

    private static OctopusTaskHistoryDetails Details()
        => new()
        {
            ActivityLogs =
            [
                new OctopusActivityLogDto
                {
                    Id = "ServerTasks-1", Name = "Deploy", Status = "Success",
                    LogElements = [new OctopusLogElementDto { Category = "Info", MessageText = "Deploying", OccurredAt = DateTimeOffset.Parse("2024-07-10T09:47:04Z") }],
                    Children =
                    [
                        new OctopusActivityLogDto { Id = "Step-1", Name = "Step 1: Deploy", Status = "SuccessWithWarning", LogElements = [new OctopusLogElementDto { Category = "Info", MessageText = "Step started" }, new OctopusLogElementDto { Category = "Warning", MessageText = "Step failed once" }] },
                        new OctopusActivityLogDto { Id = "Step-2", Name = "Step 2: Approve", Status = "Success" }
                    ]
                }
            ],
            Interruptions = [new OctopusInterruptionDto { Id = "Interruptions-1", Title = "Approve deployment", Type = "ManualIntervention", IsPending = false, Created = DateTimeOffset.Parse("2024-07-10T09:47:30Z"), Form = new OctopusInterruptionFormDto { Values = new Dictionary<string, string> { ["Result"] = "Proceed" } } }],
            Events =
            [
                new OctopusEventDto { Id = "Events-1", Category = "ManualInterventionSubmitted", Message = "approved", Occurred = DateTimeOffset.Parse("2024-07-10T09:47:40Z"), Username = "levi.y", RelatedDocumentIds = ["Interruptions-1"] },
                new OctopusEventDto { Id = "Events-2", Category = "CustomOctopusCategory", Message = "something Octopus-specific", Occurred = DateTimeOffset.Parse("2024-07-10T09:47:50Z") },
                new OctopusEventDto { Id = "Events-3", Category = "DeploymentSucceeded", Occurred = DateTimeOffset.Parse("2024-07-10T09:48:11Z"), Username = "levi.y" }
            ],
            Artifacts = [new OctopusArtifactDto { Id = "Artifacts-1", Filename = "deploy.log", Source = "Step 1", Created = DateTimeOffset.Parse("2024-07-10T09:48:00Z") }]
        };

    private const string ConfiguredVariable = """{"Id":"Variables-1","Name":"Imported variable","Value":"imported-value","Type":"String"}""";

    private const string ConfiguredStep = """{"Id":"Steps-1","Name":"Imported step","Condition":"Success","StartTrigger":"StartAfterPrevious","Actions":[{"Id":"Actions-1","Name":"Imported action","ActionType":"Octopus.Script","IsRequired":true,"Properties":{"Octopus.Action.RunOnServer":"true","Octopus.Action.Script.ScriptSource":"Inline","Octopus.Action.Script.Syntax":"Bash","Octopus.Action.Script.ScriptBody":"echo hi"}}]}""";

    private string Archive(HistorySpec spec)
    {
        var completed = spec.CompletedTime == null ? "null" : $"\"{spec.CompletedTime}\"";
        var detail = spec.DetailFingerprint == null ? string.Empty : $",\"DetailLevel\":1,\"DetailFingerprint\":\"{spec.DetailFingerprint}\"";
        var documents = new[]
        {
            Document("ProjectGroups-1", "ProjectGroup", """{"Id":"ProjectGroups-1","Name":"Idempotent group","Slug":"idempotent-group"}"""),
            Document("Environments-1", "StaticDeploymentEnvironment", """{"Id":"Environments-1","Name":"Idempotent TEST","Slug":"idempotent-test"}"""),
            Document("Lifecycles-1", "Lifecycle", """{"Id":"Lifecycles-1","Name":"Idempotent lifecycle","Slug":"idempotent-lifecycle","Phases":[{"Id":"Phases-1","Name":"TEST","AutomaticDeploymentTargets":["Environments-1"]}]}"""),
            Document("Projects-1", "Project", """{"Id":"Projects-1","Name":"Idempotent project","Slug":"idempotent-project","ProjectGroupId":"ProjectGroups-1","LifecycleId":"Lifecycles-1","VariableSetId":"variableset-Projects-1","DeploymentProcessId":"deploymentprocess-Projects-1"}"""),
            Document("variableset-Projects-1", "ProjectVariables", $$"""{"Id":"variableset-Projects-1","OwnerId":"Projects-1","OwnerType":"Project","Variables":[{{(spec.WithConfiguration ? ConfiguredVariable : "")}}]}"""),
            Document("deploymentprocess-Projects-1", "DeploymentProcess", $$"""{"Id":"deploymentprocess-Projects-1","OwnerId":"Projects-1","Steps":[{{(spec.WithConfiguration ? ConfiguredStep : "")}}]}"""),
            Document("Channels-1", "Channel", $$"""{"Id":"Channels-1","Name":"Default","Slug":"default","ProjectId":"Projects-1","IsDefault":{{(spec.MovedDefaultChannel ? "false" : "true")}}}"""),
            Document("Releases-1", "Release", $$"""{"Id":"Releases-1","ProjectId":"Projects-1","ChannelId":"Channels-1","Version":"1.0.0","ReleaseNotes":{{JsonSerializer.Serialize(spec.ReleaseNotes)}},"Assembled":"2024-07-01T08:00:00+00:00"}"""),
            Document("ServerTasks-1", "ServerTask", $$"""{"Id":"ServerTasks-1","Name":"{{spec.TaskName}}","Description":"Deploy idempotent release","ProjectId":"Projects-1","EnvironmentId":"Environments-1","State":"{{spec.State}}","QueueTime":"2024-07-10T09:47:02+00:00","StartTime":"2024-07-10T09:47:03+00:00","CompletedTime":{{completed}},"ErrorMessage":{{JsonSerializer.Serialize(spec.ErrorMessage)}},"HasWarningsOrErrors":false,"DurationSeconds":68{{detail}}}"""),
            Document("Deployments-1", "Deployment", """{"Id":"Deployments-1","Name":"Deploy to TEST","ProjectId":"Projects-1","ChannelId":"Channels-1","EnvironmentId":"Environments-1","ReleaseId":"Releases-1","TaskId":"ServerTasks-1","DeployedBy":"levi.y","DeployedById":"Users-608","DeployedToMachineIds":["Machines-1"],"Created":"2024-07-10T09:47:02+00:00"}""")
        };

        var path = Path.Combine(Path.GetTempPath(), $"squid-octopus-idempotency-{Guid.NewGuid():N}.zip");
        _paths.Add(path);

        WriteArchive(path, spec.MovedDefaultChannel ? [.. documents, .. NewDefaultChannelDocuments()] : documents);

        return path;
    }

    // A channel created in Octopus after the first import and made the project default, with one release deployed on it.
    private static (string Id, string DocumentType, string Json)[] NewDefaultChannelDocuments()
        =>
        [
            Document("Channels-9", "Channel", """{"Id":"Channels-9","Name":"Hotfix","Slug":"hotfix","ProjectId":"Projects-1","IsDefault":true}"""),
            Document("Releases-2", "Release", """{"Id":"Releases-2","ProjectId":"Projects-1","ChannelId":"Channels-9","Version":"2.0.0","ReleaseNotes":"","Assembled":"2024-08-01T08:00:00+00:00"}"""),
            Document("ServerTasks-2", "ServerTask", """{"Id":"ServerTasks-2","Name":"Deploy","Description":"Deploy hotfix release","ProjectId":"Projects-1","EnvironmentId":"Environments-1","State":"Success","QueueTime":"2024-08-10T09:47:02+00:00","StartTime":"2024-08-10T09:47:03+00:00","CompletedTime":"2024-08-10T09:48:11+00:00","ErrorMessage":"","HasWarningsOrErrors":false,"DurationSeconds":68}"""),
            Document("Deployments-2", "Deployment", """{"Id":"Deployments-2","Name":"Deploy to TEST","ProjectId":"Projects-1","ChannelId":"Channels-9","EnvironmentId":"Environments-1","ReleaseId":"Releases-2","TaskId":"ServerTasks-2","DeployedBy":"levi.y","DeployedById":"Users-608","DeployedToMachineIds":["Machines-1"],"Created":"2024-08-10T09:47:02+00:00"}""")
        ];

    private static void WriteArchive(string path, IReadOnlyList<(string Id, string DocumentType, string Json)> documents)
    {
        var manifest = JsonSerializer.Serialize(new
        {
            SchemaVersions = Array.Empty<string>(),
            InstallationId,
            Entries = documents.Select(d => new { d.Id, Name = d.Id, d.DocumentType, ExportType = "FullDocument", DocumentSource = $"{d.Id}.json", ParentId = (string)null, Hash = Sha1(d.Json) })
        });

        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        WriteEntry(archive, "manifest.json", manifest);

        foreach (var document in documents)
            WriteEntry(archive, $"{document.Id}.json", document.Json);
    }

    private static (string Id, string DocumentType, string Json) Document(string id, string documentType, string json) => (id, documentType, json);

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static string Sha1(string value) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
