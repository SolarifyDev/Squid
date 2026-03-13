using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ActivityLog;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums.Deployments;

namespace Squid.IntegrationTests.Deployments.ServerTasks;

/// <summary>
/// Integration tests verifying that ExecuteUpdateAsync (which bypasses the EF Core change tracker)
/// does not poison the change tracker when ServerTask.DataVersion is configured as IsConcurrencyToken.
///
/// Root cause scenario: If a ServerTask is loaded with tracking (FindAsync/GetByIdAsync), then
/// TransitionStateAsync updates it via ExecuteUpdateAsync (new DataVersion in DB, tracker unaware),
/// every subsequent SaveChangesAsync in the same scope fails with DbUpdateConcurrencyException
/// because the tracked entity has a stale DataVersion.
/// </summary>
public class IntegrationServerTaskChangeTracker : ServerTaskFixtureBase
{
    private async Task<int> SeedPendingTaskAsync()
    {
        var taskId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var task = new ServerTask
            {
                Name = "ChangeTracker Test",
                Description = "Tests change tracker behavior with concurrency tokens",
                QueueTime = DateTimeOffset.UtcNow,
                State = TaskState.Pending,
                ServerTaskType = "Deploy",
                ProjectId = 1,
                EnvironmentId = 1,
                SpaceId = 1,
                LastModified = DateTimeOffset.UtcNow,
                BusinessProcessState = "Queued",
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

    // ========== Tracked entity + ExecuteUpdateAsync = stale DataVersion ==========

    [Fact]
    public async Task TrackedEntity_AfterExecuteUpdateTransition_SaveChangesThrowsConcurrencyException()
    {
        var taskId = await SeedPendingTaskAsync();

        await Run<IServerTaskDataProvider, IRepository, IUnitOfWork>(async (provider, repository, unitOfWork) =>
        {
            // Load with tracking (simulates the OLD StartExecutingAsync behavior)
            var task = await provider.GetServerTaskByIdAsync(taskId);
            task.ShouldNotBeNull();

            // TransitionStateAsync uses ExecuteUpdateAsync — bypasses tracker, sets new DataVersion in DB
            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing);

            // Modify the tracked entity (like the old StartExecutingAsync did)
            task.State = TaskState.Executing;
            task.StartTime = DateTimeOffset.UtcNow;

            // Insert a new entity (like adding an activity log)
            var log = new ActivityLog
            {
                ServerTaskId = taskId,
                Name = "Test Node",
                NodeType = DeploymentActivityLogNodeType.Task,
                Status = DeploymentActivityLogNodeStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 0
            };
            await repository.InsertAsync(log);

            // SaveChangesAsync will try to UPDATE the tracked ServerTask with stale DataVersion
            // This MUST throw DbUpdateConcurrencyException — proving the bug exists
            await Should.ThrowAsync<DbUpdateConcurrencyException>(
                () => unitOfWork.SaveChangesAsync());
        });
    }

    // ========== No-tracking entity + ExecuteUpdateAsync = clean tracker ==========

    [Fact]
    public async Task NoTrackingEntity_AfterExecuteUpdateTransition_SaveChangesSucceeds()
    {
        var taskId = await SeedPendingTaskAsync();

        await Run<IServerTaskDataProvider, IRepository, IUnitOfWork>(async (provider, repository, unitOfWork) =>
        {
            // Load WITHOUT tracking (the fix: GetServerTaskByIdNoTrackingAsync)
            var task = await provider.GetServerTaskByIdNoTrackingAsync(taskId);
            task.ShouldNotBeNull();

            // TransitionStateAsync uses ExecuteUpdateAsync — no tracked entity to conflict
            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing);

            // Modify the untracked entity locally (doesn't affect change tracker)
            task.State = TaskState.Executing;
            task.StartTime = DateTimeOffset.UtcNow;

            // Insert a new entity (activity log)
            var log = new ActivityLog
            {
                ServerTaskId = taskId,
                Name = "Test Node",
                NodeType = DeploymentActivityLogNodeType.Task,
                Status = DeploymentActivityLogNodeStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 0
            };
            await repository.InsertAsync(log);

            // SaveChangesAsync should succeed — only the new ActivityLog is inserted, no stale ServerTask
            await unitOfWork.SaveChangesAsync();

            // Verify the activity log was persisted
            var persisted = await repository.QueryNoTracking<ActivityLog>(a => a.ServerTaskId == taskId)
                .FirstOrDefaultAsync();
            persisted.ShouldNotBeNull();
            persisted.Name.ShouldBe("Test Node");
        });
    }

    // ========== Pipeline-like sequence: StartExecuting → write logs → save checkpoint → completion ==========

    [Fact]
    public async Task PipelineSequence_NoTracking_AllWritesSucceed()
    {
        var taskId = await SeedPendingTaskAsync();

        await Run<IServerTaskService, IRepository, IUnitOfWork>(async (serverTaskService, repository, unitOfWork) =>
        {
            // Phase 1: StartExecutingAsync (uses no-tracking load + ExecuteUpdateAsync transition)
            var result = await serverTaskService.StartExecutingAsync(taskId);
            result.Task.ShouldNotBeNull();
            result.Task.State.ShouldBe(TaskState.Executing);

            // Phase 2: Write activity log (like DeploymentActivityLogger)
            var activityNode = new ActivityLog
            {
                ServerTaskId = taskId,
                Name = "Deploy project release 1.0 to Production",
                NodeType = DeploymentActivityLogNodeType.Task,
                Status = DeploymentActivityLogNodeStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 0
            };
            await repository.InsertAsync(activityNode);
            await unitOfWork.SaveChangesAsync();
            activityNode.Id.ShouldBeGreaterThan(0);

            // Phase 3: Write task log (like PersistTaskLogAsync)
            var taskLog = new ServerTaskLog
            {
                ServerTaskId = taskId,
                ActivityNodeId = activityNode.Id,
                Category = ServerTaskLogCategory.Info,
                MessageText = "Deploying to Production",
                Source = "System",
                OccurredAt = DateTimeOffset.UtcNow,
                SequenceNumber = 1
            };
            await repository.InsertAsync(taskLog);
            await unitOfWork.SaveChangesAsync();

            // Phase 4: Save checkpoint (like PersistCheckpointAsync)
            var checkpoint = new DeploymentExecutionCheckpoint
            {
                ServerTaskId = taskId,
                DeploymentId = 1,
                LastCompletedBatchIndex = 0,
                FailureEncountered = false,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await repository.InsertAsync(checkpoint);
            await unitOfWork.SaveChangesAsync();

            // Phase 5: Write completion (like AddDeploymentCompletionAsync)
            var completion = new DeploymentCompletion
            {
                DeploymentId = 1,
                CompletedTime = DateTimeOffset.UtcNow,
                State = TaskState.Success,
                SpaceId = 1,
                SequenceNumber = 0
            };
            await repository.InsertAsync(completion);
            await unitOfWork.SaveChangesAsync();

            // Verify all entities persisted
            var logs = await repository.QueryNoTracking<ServerTaskLog>(l => l.ServerTaskId == taskId).ToListAsync();
            logs.Count.ShouldBe(1);

            var nodes = await repository.QueryNoTracking<ActivityLog>(a => a.ServerTaskId == taskId).ToListAsync();
            nodes.Count.ShouldBe(1);

            var checkpoints = await repository.QueryNoTracking<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == taskId).ToListAsync();
            checkpoints.Count.ShouldBe(1);
        });
    }

    // ========== Concurrency token poisoning cascades to ALL writes ==========

    [Fact]
    public async Task TrackedEntity_ConcurrencyPoisoning_CascadesToAllSubsequentSaves()
    {
        var taskId = await SeedPendingTaskAsync();

        await Run<IServerTaskDataProvider, IRepository, IUnitOfWork>(async (provider, repository, unitOfWork) =>
        {
            // Load with tracking (the bug)
            var task = await provider.GetServerTaskByIdAsync(taskId);

            // Transition via ExecuteUpdateAsync — stale DataVersion in tracker
            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing);
            task.State = TaskState.Executing;

            // First write: activity log — fails silently in the real pipeline (try/catch)
            var log1 = new ActivityLog
            {
                ServerTaskId = taskId,
                Name = "Node 1",
                NodeType = DeploymentActivityLogNodeType.Task,
                Status = DeploymentActivityLogNodeStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 0
            };
            await repository.InsertAsync(log1);
            await Should.ThrowAsync<DbUpdateConcurrencyException>(() => unitOfWork.SaveChangesAsync());

            // Second write: task log — ALSO fails because log1 is still "Added" in tracker
            var log2 = new ServerTaskLog
            {
                ServerTaskId = taskId,
                Category = ServerTaskLogCategory.Info,
                MessageText = "Test log",
                Source = "System",
                OccurredAt = DateTimeOffset.UtcNow,
                SequenceNumber = 1
            };
            await repository.InsertAsync(log2);
            await Should.ThrowAsync<DbUpdateConcurrencyException>(() => unitOfWork.SaveChangesAsync());

            // Verify: NOTHING was written — zero logs, zero activity nodes
            var logs = await repository.QueryNoTracking<ServerTaskLog>(l => l.ServerTaskId == taskId).ToListAsync();
            logs.Count.ShouldBe(0);

            var nodes = await repository.QueryNoTracking<ActivityLog>(a => a.ServerTaskId == taskId).ToListAsync();
            nodes.Count.ShouldBe(0);
        });
    }

    // ========== StartExecutingAsync transitions correctly ==========

    [Fact]
    public async Task StartExecutingAsync_FromPending_TransitionsAndReturnsCorrectState()
    {
        var taskId = await SeedPendingTaskAsync();

        await Run<IServerTaskService, IServerTaskDataProvider>(async (service, provider) =>
        {
            var result = await service.StartExecutingAsync(taskId);

            result.Task.State.ShouldBe(TaskState.Executing);
            result.IsResumed.ShouldBeFalse();

            // Verify DB state
            var dbTask = await provider.GetServerTaskByIdNoTrackingAsync(taskId);
            dbTask.State.ShouldBe(TaskState.Executing);
            dbTask.StartTime.ShouldNotBeNull();
        });
    }

    [Fact]
    public async Task StartExecutingAsync_AlreadyExecuting_DoesNotThrow()
    {
        var taskId = await SeedPendingTaskAsync();

        await Run<IServerTaskService, IServerTaskDataProvider>(async (service, provider) =>
        {
            // First: transition Pending → Executing
            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing);

            // StartExecutingAsync on an already-Executing task should not throw
            var result = await service.StartExecutingAsync(taskId);
            result.Task.State.ShouldBe(TaskState.Executing);
        });
    }
}
