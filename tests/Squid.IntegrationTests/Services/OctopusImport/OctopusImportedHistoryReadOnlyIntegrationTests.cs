using System.Threading;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.Rollback;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Models.OctopusImport;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Real-Postgres coverage of how imported Octopus history behaves once it is in Squid: the source map's Created rows
/// make a task read-only (cancel / resume refused, state untouched, details flagged), while the history still feeds the
/// consumers it exists for — lifecycle progression and rollback-target selection. History is written by the real
/// importer and recorded in the source map exactly as a confirmation does.
/// </summary>
public class OctopusImportedHistoryReadOnlyIntegrationTests : TestBase
{
    private const int SpaceId = 1;

    public OctopusImportedHistoryReadOnlyIntegrationTests() : base("OctopusImportedHistoryReadOnly", "squid_it_octopus_imported_history_read_only")
    {
    }

    [Theory]
    [InlineData("ServerTask", "Created", true)]
    [InlineData("ServerTask", "Reused", false)]
    [InlineData("Deployment", "Created", false)]
    [InlineData(null, null, false)]
    public async Task IsImported_OnlyACreatedServerTaskRowCounts(string sourceType, string outcome, bool expected)
    {
        var taskId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) => taskId = (await new TestDataBuilder(repository, unitOfWork).CreateServerTaskAsync(TaskState.Executing)).Id);

        if (sourceType != null) await RecordAsync(sourceType, $"Source-{Guid.NewGuid():N}", taskId, outcome);

        (await Run<IServerTaskDataProvider, bool>(provider => provider.IsImportedAsync(taskId))).ShouldBe(expected);
    }

    [Fact]
    public async Task ImportedExecutingTask_CancelAndResumeRefused_StateUntouched_DetailsFlagged()
    {
        var scenario = await SeedScenarioAsync();
        var imported = await ImportAsync(scenario, scenario.ReleaseIds[0], TaskState.Executing, DateTimeOffset.UtcNow.AddDays(-2));

        await Run<IServerTaskControlService>(async control =>
        {
            await Should.ThrowAsync<ImportedServerTaskReadOnlyException>(() => control.CancelTaskAsync(imported.TaskId));
            await Should.ThrowAsync<ImportedServerTaskReadOnlyException>(() => control.ResumeTaskAsync(imported.TaskId));
        });

        var task = await Run<IRepository, ServerTaskEntity>(repository => repository.QueryNoTracking<ServerTaskEntity>(t => t.Id == imported.TaskId).SingleAsync());

        task.State.ShouldBe(TaskState.Executing, customMessage: "An imported task keeps its original Octopus state; a refused cancel must not move it.");
        task.ConcurrencyTag.ShouldBeNull(customMessage: "An imported task never holds an environment concurrency slot.");

        var details = await Run<IServerTaskService, Squid.Message.Models.Deployments.ServerTask.ServerTaskDetailsDto>(service => service.GetTaskDetailsAsync(imported.TaskId));

        details.Task.IsImported.ShouldBeTrue();
    }

    [Fact]
    public async Task LifecycleProgression_CountsImportedSuccess()
    {
        var scenario = await SeedScenarioAsync();

        await ImportAsync(scenario, scenario.ReleaseIds[0], TaskState.Success, DateTimeOffset.UtcNow.AddDays(-3));

        var progression = await Run<ILifecycleProgressionEvaluator, PhaseProgressionResult>(evaluator => evaluator.EvaluateProgressionForReleaseAsync(scenario.LifecycleId, scenario.ReleaseIds[0], CancellationToken.None));

        progression.Phases.ShouldHaveSingleItem().IsComplete.ShouldBeTrue(customMessage: "An imported successful deployment completes its lifecycle phase, exactly like a native one.");
        progression.Phases[0].DeployedEnvironmentIds.ShouldContain(scenario.EnvironmentId);
    }

    [Fact]
    public async Task Rollback_SelectsImportedRelease()
    {
        var scenario = await SeedScenarioAsync();

        await ImportAsync(scenario, scenario.ReleaseIds[0], TaskState.Success, DateTimeOffset.UtcNow.AddDays(-3));
        await ImportAsync(scenario, scenario.ReleaseIds[1], TaskState.Success, DateTimeOffset.UtcNow.AddDays(-1));

        var target = await Run<IRollbackService, RollbackReleaseHistoryEntry>(rollback => rollback.GetRollbackTargetAsync(scenario.ProjectId, scenario.EnvironmentId));

        target.ShouldNotBeNull(customMessage: "Imported successful deployments form the rollback journal.");
        target.ReleaseId.ShouldBe(scenario.ReleaseIds[0], customMessage: "The previous distinct release is the older imported one.");
    }

    // ── seeding helpers ──

    private sealed record Scenario(int ProjectId, int ChannelId, int EnvironmentId, int LifecycleId, int[] ReleaseIds);

    private async Task<Scenario> SeedScenarioAsync()
    {
        Scenario scenario = null;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);
            var suffix = Guid.NewGuid().ToString("N");

            var lifecycle = await builder.CreateLifecycleAsync($"lifecycle-{suffix}");
            var environment = await builder.CreateEnvironmentAsync($"env-{suffix}");
            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, environment.Id, $"phase-{suffix}");

            var group = await builder.CreateProjectGroupAsync($"group-{suffix}");
            var variableSet = await builder.CreateVariableSetAsync();
            var project = await builder.CreateProjectAsync(variableSet.Id, 0, group.Id, lifecycle.Id, $"project-{suffix}");
            var channel = await builder.CreateChannelAsync(project.Id, lifecycle.Id, $"channel-{suffix}");

            var first = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0");
            var second = await builder.CreateReleaseAsync(project.Id, channel.Id, "2.0.0");

            scenario = new Scenario(project.Id, channel.Id, environment.Id, lifecycle.Id, [first.Id, second.Id]);
        });

        return scenario;
    }

    private async Task<OctopusImportDeploymentHistoryResult> ImportAsync(Scenario scenario, int releaseId, string state, DateTimeOffset queued)
    {
        var completed = TaskState.IsTerminal(state) ? queued.AddMinutes(2) : (DateTimeOffset?)null;
        var deploymentSourceId = $"Deployments-{Guid.NewGuid():N}";

        var request = new OctopusImportDeploymentHistoryRequest(SpaceId, scenario.ProjectId, scenario.ChannelId, releaseId, scenario.EnvironmentId, deploymentSourceId, "{}", "alice", "Users-1", """["Machines-1"]""", queued, "Deploy", $"Deploy {deploymentSourceId}", queued, queued.AddSeconds(1), completed, state, string.Empty, false, 0);

        var result = await Run<IOctopusImportDeploymentHistoryImporter, OctopusImportDeploymentHistoryResult>(importer => importer.ImportAsync(request));

        await RecordAsync("Deployment", deploymentSourceId, result.DeploymentId, OctopusImportSourceMapQueries.CreatedOutcome);
        await RecordAsync("ServerTask", $"ServerTasks-{Guid.NewGuid():N}", result.TaskId, OctopusImportSourceMapQueries.CreatedOutcome);

        return result;
    }

    private Task RecordAsync(string sourceType, string sourceId, int destinationId, string outcome)
        => Run<IOctopusImportSourceMapDataProvider>(provider => provider.UpsertAsync([new OctopusImportSourceMapRow(SpaceId, sourceType, sourceId, destinationId, outcome)], Guid.NewGuid()));
}
