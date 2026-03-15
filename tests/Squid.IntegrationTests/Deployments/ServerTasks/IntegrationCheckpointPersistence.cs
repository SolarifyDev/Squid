using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.IntegrationTests.Deployments.ServerTasks;

/// <summary>
/// Integration tests for DeploymentCheckpointService persistence behavior.
/// Verifies the upsert pattern (ExecuteUpdateAsync → fallback InsertAsync) works correctly
/// and that the change tracker is not poisoned by checkpoint operations.
/// </summary>
public class IntegrationCheckpointPersistence : ServerTaskFixtureBase
{
    private async Task<int> SeedExecutingTaskAsync()
    {
        var taskId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var task = new ServerTask
            {
                Name = "Checkpoint Test Task",
                Description = "Task for checkpoint integration test",
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

    // ========== SaveAsync — Insert path (first save) ==========

    [Fact]
    public async Task SaveCheckpoint_FirstSave_InsertsNewRecord()
    {
        var taskId = await SeedExecutingTaskAsync();

        await Run<IDeploymentCheckpointService, IRepository>(async (service, repository) =>
        {
            await service.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = taskId,
                DeploymentId = 1,
                LastCompletedBatchIndex = 0,
                FailureEncountered = false,

            });

            var checkpoint = await repository.QueryNoTracking<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == taskId)
                .FirstOrDefaultAsync();

            checkpoint.ShouldNotBeNull();
            checkpoint.LastCompletedBatchIndex.ShouldBe(0);
            checkpoint.FailureEncountered.ShouldBeFalse();
        });
    }

    // ========== SaveAsync — Update path (subsequent saves) ==========

    [Fact]
    public async Task SaveCheckpoint_SubsequentSave_UpdatesViaExecuteUpdate()
    {
        var taskId = await SeedExecutingTaskAsync();

        await Run<IDeploymentCheckpointService, IRepository>(async (service, repository) =>
        {
            // First save: INSERT path
            await service.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = taskId,
                DeploymentId = 1,
                LastCompletedBatchIndex = 0,
                FailureEncountered = false,

            });

            // Second save: ExecuteUpdateAsync path (bypasses tracker)
            await service.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = taskId,
                DeploymentId = 1,
                LastCompletedBatchIndex = 1,
                FailureEncountered = true,

            });

            var checkpoint = await repository.QueryNoTracking<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == taskId)
                .FirstOrDefaultAsync();

            checkpoint.ShouldNotBeNull();
            checkpoint.LastCompletedBatchIndex.ShouldBe(1);
            checkpoint.FailureEncountered.ShouldBeTrue();
        });
    }

    // ========== SaveAsync does not poison the change tracker ==========

    [Fact]
    public async Task SaveCheckpoint_DoesNotPoisonTrackerForSubsequentSaves()
    {
        var taskId = await SeedExecutingTaskAsync();

        await Run<IDeploymentCheckpointService, IRepository, IUnitOfWork>(async (service, repository, unitOfWork) =>
        {
            // Save checkpoint (uses InsertAsync + SaveChangesAsync internally)
            await service.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = taskId,
                DeploymentId = 1,
                LastCompletedBatchIndex = 0,
                FailureEncountered = false,

            });

            // Subsequent save via ExecuteUpdateAsync (bypasses tracker)
            await service.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = taskId,
                DeploymentId = 1,
                LastCompletedBatchIndex = 1,
                FailureEncountered = false,

            });

            // Now insert a DIFFERENT entity — this should work without duplicate key errors
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

            // Verify completion was saved
            var persisted = await repository.QueryNoTracking<DeploymentCompletion>(c => c.DeploymentId == 1)
                .FirstOrDefaultAsync();
            persisted.ShouldNotBeNull();
        });
    }

    // ========== DeleteAsync ==========

    [Fact]
    public async Task DeleteCheckpoint_RemovesRecord()
    {
        var taskId = await SeedExecutingTaskAsync();

        await Run<IDeploymentCheckpointService, IRepository>(async (service, repository) =>
        {
            // Insert
            await service.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = taskId,
                DeploymentId = 1,
                LastCompletedBatchIndex = 0,
                FailureEncountered = false,

            });

            // Delete via ExecuteDeleteAsync
            await service.DeleteAsync(taskId);

            var checkpoint = await repository.QueryNoTracking<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == taskId)
                .FirstOrDefaultAsync();
            checkpoint.ShouldBeNull();
        });
    }

    [Fact]
    public async Task DeleteCheckpoint_NonExistent_DoesNotThrow()
    {
        await Run<IDeploymentCheckpointService>(async service =>
        {
            // Should not throw even if no checkpoint exists
            await service.DeleteAsync(999999);
        });
    }

    // ========== LoadAsync ==========

    [Fact]
    public async Task LoadCheckpoint_ReturnsPersistedData()
    {
        var taskId = await SeedExecutingTaskAsync();

        await Run<IDeploymentCheckpointService>(async service =>
        {
            var saved = new DeploymentExecutionCheckpoint
            {
                ServerTaskId = taskId,
                DeploymentId = 1,
                LastCompletedBatchIndex = 3,
                FailureEncountered = true,
                OutputVariablesJson = """[{"Name":"Squid.Action.Step1.Var","Value":"test"}]""",

            };

            await service.SaveAsync(saved);

            var loaded = await service.LoadAsync(taskId);

            loaded.ShouldNotBeNull();
            loaded.ServerTaskId.ShouldBe(taskId);
            loaded.LastCompletedBatchIndex.ShouldBe(3);
            loaded.FailureEncountered.ShouldBeTrue();
            loaded.OutputVariablesJson.ShouldContain("Squid.Action.Step1.Var");
        });
    }

    [Fact]
    public async Task LoadCheckpoint_NonExistent_ReturnsNull()
    {
        await Run<IDeploymentCheckpointService>(async service =>
        {
            var result = await service.LoadAsync(999999);
            result.ShouldBeNull();
        });
    }

    // ========== Full checkpoint lifecycle ==========

    [Fact]
    public async Task CheckpointLifecycle_Save_Update_Load_Delete()
    {
        var taskId = await SeedExecutingTaskAsync();

        await Run<IDeploymentCheckpointService>(async service =>
        {
            // Create
            await service.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = taskId,
                DeploymentId = 1,
                LastCompletedBatchIndex = 0,
                FailureEncountered = false,

            });

            // Update (second save → ExecuteUpdateAsync path)
            await service.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = taskId,
                DeploymentId = 1,
                LastCompletedBatchIndex = 2,
                FailureEncountered = true,
                OutputVariablesJson = """{"key":"value"}""",

            });

            // Load and verify updated state
            var loaded = await service.LoadAsync(taskId);
            loaded.ShouldNotBeNull();
            loaded.LastCompletedBatchIndex.ShouldBe(2);
            loaded.FailureEncountered.ShouldBeTrue();

            // Delete
            await service.DeleteAsync(taskId);

            // Verify deleted
            var deleted = await service.LoadAsync(taskId);
            deleted.ShouldBeNull();
        });
    }
}
