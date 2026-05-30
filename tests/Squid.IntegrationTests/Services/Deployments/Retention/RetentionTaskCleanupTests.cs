using System;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.IntegrationTests.Base;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums.Deployments;
using ProjectEntity = Squid.Core.Persistence.Entities.Deployments.Project;

namespace Squid.IntegrationTests.Services.Deployments.Retention;

/// <summary>
/// Integration coverage for release-retention pruning the server-task data tied to each
/// pruned deployment. Against a real Postgres DB: when <c>RetentionPolicyEnforcer</c>
/// deletes a deployment past its lifecycle's release-retention window, it also deletes that
/// deployment's <c>ServerTask</c> + <c>ActivityLog</c> tree + <c>ServerTaskLog</c> lines +
/// <c>DeploymentInterruption</c> + <c>DeploymentExecutionCheckpoint</c>. Deployments within
/// the window, or preserved because their release is currently deployed, keep their task
/// data. Multiple aged deployments are all pruned; an aged deployment with no task is pruned
/// without error. Each test seeds its own isolated project graph.
/// </summary>
public class RetentionTaskCleanupTests : TestBase
{
    public RetentionTaskCleanupTests() : base("RetentionTaskCleanup", "squid_it_retention_task_cleanup")
    {
    }

    [Fact]
    public async Task OldDeployment_PrunesDeploymentAndAllTaskData()
    {
        var graph = await SeedGraphAsync().ConfigureAwait(false);
        var (deploymentId, taskId) = await SeedAgedDeploymentAsync(graph, ageDays: 40).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await DeploymentExistsAsync(deploymentId).ConfigureAwait(false)).ShouldBeFalse(
            customMessage: "A deployment past its release-retention window must be pruned.");
        (await TaskExistsAsync(taskId).ConfigureAwait(false)).ShouldBeFalse(
            customMessage: "The pruned deployment's ServerTask must be deleted with it.");
        (await ActivityCountAsync(taskId).ConfigureAwait(false)).ShouldBe(0, customMessage: "Activity-log tree must be pruned with the task.");
        (await LogCountAsync(taskId).ConfigureAwait(false)).ShouldBe(0, customMessage: "Task-log lines must be pruned with the task.");
        (await InterruptionCountAsync(taskId).ConfigureAwait(false)).ShouldBe(0, customMessage: "Manual-intervention / guided-failure rows must be pruned with the task, not orphaned.");
        (await CheckpointCountAsync(taskId).ConfigureAwait(false)).ShouldBe(0, customMessage: "Execution checkpoints must be pruned with the task, not orphaned.");
    }

    [Fact]
    public async Task RecentDeployment_KeepsDeploymentAndTaskData()
    {
        var graph = await SeedGraphAsync().ConfigureAwait(false);
        var (deploymentId, taskId) = await SeedAgedDeploymentAsync(graph, ageDays: 5).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await DeploymentExistsAsync(deploymentId).ConfigureAwait(false)).ShouldBeTrue();
        (await TaskExistsAsync(taskId).ConfigureAwait(false)).ShouldBeTrue();
        (await ActivityCountAsync(taskId).ConfigureAwait(false)).ShouldBe(1);
        (await LogCountAsync(taskId).ConfigureAwait(false)).ShouldBe(1);
        (await InterruptionCountAsync(taskId).ConfigureAwait(false)).ShouldBe(1);
        (await CheckpointCountAsync(taskId).ConfigureAwait(false)).ShouldBe(1);
    }

    [Fact]
    public async Task CurrentlyDeployedRelease_KeepsTaskDataEvenWhenOld()
    {
        var graph = await SeedGraphAsync().ConfigureAwait(false);
        var (deploymentId, taskId) = await SeedAgedDeploymentAsync(graph, ageDays: 40, currentlyDeployed: true).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await DeploymentExistsAsync(deploymentId).ConfigureAwait(false)).ShouldBeTrue(
            customMessage: "A currently-deployed release is preserved by retention, so its task data must survive too.");
        (await TaskExistsAsync(taskId).ConfigureAwait(false)).ShouldBeTrue();
        (await LogCountAsync(taskId).ConfigureAwait(false)).ShouldBe(1);
        (await CheckpointCountAsync(taskId).ConfigureAwait(false)).ShouldBe(1);
    }

    [Fact]
    public async Task MultipleAgedDeployments_AllPruned()
    {
        var graph = await SeedGraphAsync().ConfigureAwait(false);
        var first = await SeedAgedDeploymentAsync(graph, ageDays: 40).ConfigureAwait(false);
        var second = await SeedAgedDeploymentAsync(graph, ageDays: 50).ConfigureAwait(false);
        var third = await SeedAgedDeploymentAsync(graph, ageDays: 60).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        foreach (var (deploymentId, taskId) in new[] { first, second, third })
        {
            (await DeploymentExistsAsync(deploymentId).ConfigureAwait(false)).ShouldBeFalse(
                customMessage: "Every aged deployment in the batch must be pruned.");
            (await TaskExistsAsync(taskId).ConfigureAwait(false)).ShouldBeFalse(
                customMessage: "Every aged deployment's task must be pruned (multi-id IN clause).");
            (await LogCountAsync(taskId).ConfigureAwait(false)).ShouldBe(0);
            (await ActivityCountAsync(taskId).ConfigureAwait(false)).ShouldBe(0);
        }
    }

    [Fact]
    public async Task AgedDeploymentWithoutTask_PrunedWithoutError()
    {
        var graph = await SeedGraphAsync().ConfigureAwait(false);
        var deploymentId = await SeedAgedDeploymentWithoutTaskAsync(graph, ageDays: 40).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await DeploymentExistsAsync(deploymentId).ConfigureAwait(false)).ShouldBeFalse(
            customMessage: "An aged deployment with no task (TaskId null) must still be pruned, with no task-data delete attempted.");
    }

    // ── enforce / assertion helpers ──

    private Task EnforceAsync(int projectId)
        => Run<IRetentionPolicyEnforcer>(enforcer => enforcer.EnforceRetentionForProjectAsync(projectId, CancellationToken.None));

    private Task<bool> DeploymentExistsAsync(int id)
        => Run<IRepository, bool>(repo => repo.AnyAsync<Deployment>(d => d.Id == id, CancellationToken.None));

    private Task<bool> TaskExistsAsync(int id)
        => Run<IRepository, bool>(repo => repo.AnyAsync<ServerTask>(t => t.Id == id, CancellationToken.None));

    private Task<int> ActivityCountAsync(int taskId)
        => Run<IRepository, int>(repo => repo.CountAsync<ActivityLog>(a => a.ServerTaskId == taskId, CancellationToken.None));

    private Task<int> LogCountAsync(int taskId)
        => Run<IRepository, int>(repo => repo.CountAsync<ServerTaskLog>(l => l.ServerTaskId == taskId, CancellationToken.None));

    private Task<int> InterruptionCountAsync(int taskId)
        => Run<IRepository, int>(repo => repo.CountAsync<DeploymentInterruption>(i => i.ServerTaskId == taskId, CancellationToken.None));

    private Task<int> CheckpointCountAsync(int taskId)
        => Run<IRepository, int>(repo => repo.CountAsync<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == taskId, CancellationToken.None));

    // ── seeding helpers ──

    private async Task<(int ProjectId, int EnvironmentId, int ChannelId)> SeedGraphAsync()
    {
        var graph = (ProjectId: 0, EnvironmentId: 0, ChannelId: 0);

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var builder = new TestDataBuilder(repo, uow);
            var suffix = Guid.NewGuid().ToString("N");

            var environment = await builder.CreateEnvironmentAsync($"env-{suffix}").ConfigureAwait(false);

            var lifecycle = new Lifecycle
            {
                Name = $"lifecycle-{suffix}",
                SpaceId = 1,
                Slug = $"lifecycle-{suffix}",
                ReleaseRetentionKeepForever = false,
                ReleaseRetentionUnit = RetentionPolicyUnit.Days,
                ReleaseRetentionQuantity = 30,
                TentacleRetentionKeepForever = true
            };
            await repo.InsertAsync(lifecycle, CancellationToken.None).ConfigureAwait(false);
            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, environment.Id, $"phase-{suffix}").ConfigureAwait(false);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);

            var project = new ProjectEntity
            {
                Name = $"project-{suffix}",
                Slug = $"project-{suffix}",
                IsDisabled = false,
                VariableSetId = variableSet.Id,
                DeploymentProcessId = 0,
                ProjectGroupId = 1,
                LifecycleId = lifecycle.Id,
                AutoCreateRelease = false,
                Json = string.Empty,
                IncludedLibraryVariableSetIds = "[]",
                DiscreteChannelRelease = false,
                SpaceId = 1,
                LastModifiedDate = DateTimeOffset.UtcNow,
                AllowIgnoreChannelRules = false
            };
            await repo.InsertAsync(project, CancellationToken.None).ConfigureAwait(false);
            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, lifecycle.Id, $"channel-{suffix}").ConfigureAwait(false);

            graph = (project.Id, environment.Id, channel.Id);
        }).ConfigureAwait(false);

        return graph;
    }

    private async Task<(int DeploymentId, int TaskId)> SeedAgedDeploymentAsync((int ProjectId, int EnvironmentId, int ChannelId) graph, int ageDays, bool currentlyDeployed = false)
    {
        var result = (DeploymentId: 0, TaskId: 0);

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var builder = new TestDataBuilder(repo, uow);

            var release = await builder.CreateReleaseAsync(graph.ProjectId, graph.ChannelId, $"1.0.{Guid.NewGuid():N}").ConfigureAwait(false);
            var task = await builder.CreateServerTaskAsync("Success").ConfigureAwait(false);

            await repo.InsertAsync(new ActivityLog
            {
                ServerTaskId = task.Id,
                Name = "root",
                NodeType = DeploymentActivityLogNodeType.Task,
                Category = DeploymentActivityLogCategory.Info,
                Status = DeploymentActivityLogNodeStatus.Success,
                StartedAt = DateTimeOffset.UtcNow.AddDays(-ageDays),
                SortOrder = 0,
                LastModifiedDate = DateTimeOffset.UtcNow
            }, CancellationToken.None).ConfigureAwait(false);

            await repo.InsertAsync(new ServerTaskLog
            {
                ServerTaskId = task.Id,
                Category = ServerTaskLogCategory.Info,
                MessageText = "deployment log line",
                Source = "test",
                OccurredAt = DateTimeOffset.UtcNow.AddDays(-ageDays),
                SequenceNumber = 1,
                LastModifiedDate = DateTimeOffset.UtcNow
            }, CancellationToken.None).ConfigureAwait(false);

            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var deployment = await builder.CreateDeploymentAsync(graph.ProjectId, graph.EnvironmentId, release.Id, task.Id, graph.ChannelId).ConfigureAwait(false);

            await repo.InsertAsync(new DeploymentInterruption
            {
                ServerTaskId = task.Id,
                DeploymentId = deployment.Id,
                StepName = "step",
                ActionName = "action",
                MachineName = "machine",
                ErrorMessage = string.Empty,
                FormJson = "{}",
                SubmittedValuesJson = "{}",
                ResponsibleUserId = string.Empty,
                ResponsibleTeamIds = string.Empty,
                Resolution = string.Empty,
                SpaceId = 1,
                LastModifiedDate = DateTimeOffset.UtcNow
            }, CancellationToken.None).ConfigureAwait(false);

            await repo.InsertAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = task.Id,
                DeploymentId = deployment.Id,
                LastCompletedBatchIndex = 0,
                FailureEncountered = false,
                OutputVariablesJson = "{}",
                LastModifiedDate = DateTimeOffset.UtcNow
            }, CancellationToken.None).ConfigureAwait(false);

            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            await repo.ExecuteUpdateAsync<Deployment>(
                d => d.Id == deployment.Id,
                s => s.SetProperty(d => d.CreatedDate, DateTimeOffset.UtcNow.AddDays(-ageDays)),
                CancellationToken.None).ConfigureAwait(false);

            if (currentlyDeployed)
                await builder.CreateDeploymentCompletionAsync(deployment.Id, "Success").ConfigureAwait(false);

            result = (deployment.Id, task.Id);
        }).ConfigureAwait(false);

        return result;
    }

    private async Task<int> SeedAgedDeploymentWithoutTaskAsync((int ProjectId, int EnvironmentId, int ChannelId) graph, int ageDays)
    {
        var deploymentId = 0;

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var builder = new TestDataBuilder(repo, uow);

            var release = await builder.CreateReleaseAsync(graph.ProjectId, graph.ChannelId, $"1.0.{Guid.NewGuid():N}").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "no-task",
                TaskId = null,
                SpaceId = 1,
                ChannelId = graph.ChannelId,
                ProjectId = graph.ProjectId,
                ReleaseId = release.Id,
                EnvironmentId = graph.EnvironmentId,
                MachineId = 0,
                Json = "{}",
                DeployedBy = 0,
                DeployedToMachineIds = string.Empty,
                CreatedDate = DateTimeOffset.UtcNow
            };
            await repo.InsertAsync(deployment, CancellationToken.None).ConfigureAwait(false);
            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            await repo.ExecuteUpdateAsync<Deployment>(
                d => d.Id == deployment.Id,
                s => s.SetProperty(d => d.CreatedDate, DateTimeOffset.UtcNow.AddDays(-ageDays)),
                CancellationToken.None).ConfigureAwait(false);

            deploymentId = deployment.Id;
        }).ConfigureAwait(false);

        return deploymentId;
    }
}
