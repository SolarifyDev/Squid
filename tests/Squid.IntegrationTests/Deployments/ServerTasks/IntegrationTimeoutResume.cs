using System;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.IntegrationTests.Deployments.ServerTasks;

/// <summary>
/// Integration coverage for the resumable-timeout completion path against a REAL
/// Postgres DB. <c>DeploymentCompletionHandlerTests</c> proves the branch logic
/// with mocks; this proves the persistence truth that matters operationally:
/// a timed-out deployment ends <see cref="TaskState.Paused"/> (a resumable,
/// non-terminal state) with its checkpoint row INTACT, so the deployment can be
/// resumed from the last completed batch instead of restarting from scratch.
/// The contrast test pins the fail-fast escape-hatch behaviour
/// (<c>OnFailureAsync</c>): Failed + checkpoint deleted = unrecoverable.
/// </summary>
public class IntegrationTimeoutResume : ServerTaskFixtureBase
{
    [Fact]
    public async Task OnTimedOut_PausesTask_AndPreservesCheckpoint()
    {
        var taskId = await SeedExecutingTaskAsync();

        await Run<IDeploymentCheckpointService>(async checkpointService =>
        {
            await checkpointService.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = taskId,
                DeploymentId = 1,
                LastCompletedBatchIndex = 1,
                FailureEncountered = false
            });
        });

        await Run<IDeploymentCompletionHandler>(async handler =>
        {
            var ctx = new DeploymentTaskContext { ServerTaskId = taskId };
            await handler.OnTimedOutAsync(ctx, new DeploymentTimeoutException(taskId, TimeSpan.FromMinutes(60)), CancellationToken.None);
        });

        await Run<IRepository, IDeploymentCheckpointService>(async (repository, checkpointService) =>
        {
            var task = await repository.QueryNoTracking<ServerTask>(t => t.Id == taskId).FirstOrDefaultAsync();
            task.ShouldNotBeNull();
            task.State.ShouldBe(TaskState.Paused,
                customMessage: "A timed-out deployment must end Paused (resumable), not Failed. " +
                              "If Failed, the timeout routed through OnFailureAsync instead of OnTimedOutAsync.");

            var checkpoint = await checkpointService.LoadAsync(taskId);
            checkpoint.ShouldNotBeNull(
                customMessage: "Checkpoint MUST survive a timeout-pause — it is the resume point. " +
                              "If null, OnTimedOutAsync wrongly deleted it (the unrecoverable regression this fixes).");
            checkpoint.LastCompletedBatchIndex.ShouldBe(1,
                customMessage: "Preserved checkpoint must retain its progress so resume skips completed batches.");
        });
    }

    [Fact]
    public async Task OnFailure_FailsTask_AndDeletesCheckpoint()
    {
        // Pins the fail-fast escape hatch (SQUID_DEPLOYMENT_TIMEOUT_RESUMABLE=false
        // routes timeouts here): the historical Failed + checkpoint-deleted
        // behaviour, intentionally unrecoverable.
        var taskId = await SeedExecutingTaskAsync();

        await Run<IDeploymentCheckpointService>(async checkpointService =>
        {
            await checkpointService.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = taskId,
                DeploymentId = 1,
                LastCompletedBatchIndex = 1,
                FailureEncountered = false
            });
        });

        await Run<IDeploymentCompletionHandler>(async handler =>
        {
            var ctx = new DeploymentTaskContext { ServerTaskId = taskId };
            await handler.OnFailureAsync(ctx, new DeploymentTimeoutException(taskId, TimeSpan.FromMinutes(60)), CancellationToken.None);
        });

        await Run<IRepository, IDeploymentCheckpointService>(async (repository, checkpointService) =>
        {
            var task = await repository.QueryNoTracking<ServerTask>(t => t.Id == taskId).FirstOrDefaultAsync();
            task.ShouldNotBeNull();
            task.State.ShouldBe(TaskState.Failed);

            var checkpoint = await checkpointService.LoadAsync(taskId);
            checkpoint.ShouldBeNull("Fail-fast path must delete the checkpoint (historical behaviour).");
        });
    }

    private async Task<int> SeedExecutingTaskAsync()
    {
        var taskId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var task = new ServerTask
            {
                Name = "Timeout Resume Test Task",
                Description = "Task for timeout-resume integration test",
                QueueTime = DateTimeOffset.UtcNow,
                State = TaskState.Executing,
                StartTime = DateTimeOffset.UtcNow,
                ServerTaskType = "Deploy",
                ProjectId = 1,
                EnvironmentId = 1,
                SpaceId = 1,
                LastModifiedDate = DateTimeOffset.UtcNow,
                BusinessProcessState = "Executing",
                StateOrder = 1,
                Weight = 1,
                BatchId = 0,
                JSON = string.Empty,
                HasWarningsOrErrors = false,
                ServerNodeId = Guid.NewGuid(),
                DurationSeconds = 0,
                DataVersion = Guid.NewGuid().ToByteArray()
            };

            await repository.InsertAsync(task);
            await unitOfWork.SaveChangesAsync();
            taskId = task.Id;
        });

        return taskId;
    }
}
