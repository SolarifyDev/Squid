using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.IntegrationTests.Deployments.ServerTasks;

public class IntegrationServerTaskState : ServerTaskFixtureBase
{
    private async Task<int> CreatePendingTaskAsync()
    {
        var taskId = 0;
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var task = new ServerTask
            {
                Name = "State Test Task",
                Description = "Task for state management integration test",
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

            await repository.InsertAsync(task, CancellationToken.None).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            taskId = task.Id;
        }).ConfigureAwait(false);

        return taskId;
    }

    // ========== TransitionStateAsync — Valid Transitions ==========

    [Fact]
    public async Task TransitionState_PendingToExecuting_Success()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing)
                .ConfigureAwait(false);

            var task = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            task.State.ShouldBe(TaskState.Executing);
            task.CompletedTime.ShouldBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task TransitionState_ExecutingToSuccess_SetsCompletedTime()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing)
                .ConfigureAwait(false);
            await provider.TransitionStateAsync(taskId, TaskState.Executing, TaskState.Success)
                .ConfigureAwait(false);

            var task = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            task.State.ShouldBe(TaskState.Success);
            task.CompletedTime.ShouldNotBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task TransitionState_ExecutingToFailed_SetsCompletedTime()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing)
                .ConfigureAwait(false);
            await provider.TransitionStateAsync(taskId, TaskState.Executing, TaskState.Failed)
                .ConfigureAwait(false);

            var task = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            task.State.ShouldBe(TaskState.Failed);
            task.CompletedTime.ShouldNotBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task TransitionState_FullCancellationPath()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing)
                .ConfigureAwait(false);
            await provider.TransitionStateAsync(taskId, TaskState.Executing, TaskState.Cancelling)
                .ConfigureAwait(false);

            var midTask = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            midTask.State.ShouldBe(TaskState.Cancelling);
            midTask.CompletedTime.ShouldBeNull();

            await provider.TransitionStateAsync(taskId, TaskState.Cancelling, TaskState.Cancelled)
                .ConfigureAwait(false);

            var finalTask = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            finalTask.State.ShouldBe(TaskState.Cancelled);
            finalTask.CompletedTime.ShouldNotBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task TransitionState_PendingToCancelled()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Cancelled)
                .ConfigureAwait(false);

            var task = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            task.State.ShouldBe(TaskState.Cancelled);
            task.CompletedTime.ShouldNotBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task TransitionState_PendingToTimedOut()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.TimedOut)
                .ConfigureAwait(false);

            var task = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            task.State.ShouldBe(TaskState.TimedOut);
        }).ConfigureAwait(false);
    }

    // ========== TransitionStateAsync — Invalid Transitions ==========

    [Fact]
    public async Task TransitionState_InvalidTransition_Throws()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            await Should.ThrowAsync<InvalidStateTransitionException>(async () =>
                await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Success)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            // State should remain unchanged
            var task = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            task.State.ShouldBe(TaskState.Pending);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task TransitionState_BackwardTransition_Throws()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing)
                .ConfigureAwait(false);
            await provider.TransitionStateAsync(taskId, TaskState.Executing, TaskState.Success)
                .ConfigureAwait(false);

            await Should.ThrowAsync<InvalidStateTransitionException>(async () =>
                await provider.TransitionStateAsync(taskId, TaskState.Success, TaskState.Executing)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            var task = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            task.State.ShouldBe(TaskState.Success);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task TransitionState_TerminalStateCannotTransition_Throws()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing)
                .ConfigureAwait(false);
            await provider.TransitionStateAsync(taskId, TaskState.Executing, TaskState.Failed)
                .ConfigureAwait(false);

            await Should.ThrowAsync<InvalidStateTransitionException>(async () =>
                await provider.TransitionStateAsync(taskId, TaskState.Failed, TaskState.Pending)
                    .ConfigureAwait(false)).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    // ========== State Mismatch ==========

    [Fact]
    public async Task TransitionState_StateMismatch_Throws()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            // Task is Pending, but we claim it's Executing
            await Should.ThrowAsync<InvalidStateTransitionException>(async () =>
                await provider.TransitionStateAsync(taskId, TaskState.Executing, TaskState.Success)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            var task = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            task.State.ShouldBe(TaskState.Pending);
        }).ConfigureAwait(false);
    }

    // ========== Task Not Found ==========

    [Fact]
    public async Task TransitionState_NonExistentTask_Throws()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            await Should.ThrowAsync<DeploymentEntityNotFoundException>(async () =>
                await provider.TransitionStateAsync(999999, TaskState.Pending, TaskState.Executing)
                    .ConfigureAwait(false)).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    // ========== DataVersion Concurrency ==========

    [Fact]
    public async Task TransitionState_UpdatesDataVersion()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            var before = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            var originalVersion = before.DataVersion;

            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing)
                .ConfigureAwait(false);

            var after = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);

            after.DataVersion.ShouldNotBe(originalVersion);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task TransitionState_UpdatesLastModified()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            var before = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            var originalModified = before.LastModified;

            await Task.Delay(10).ConfigureAwait(false);

            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing)
                .ConfigureAwait(false);

            var after = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            after.LastModified.ShouldBeGreaterThan(originalModified);
        }).ConfigureAwait(false);
    }

    // ========== GetAndLockPendingTaskAsync ==========

    [Fact]
    public async Task GetAndLock_TransitionsPendingToExecuting()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            await CreatePendingTaskAsync().ConfigureAwait(false);

            var locked = await provider.GetAndLockPendingTaskAsync().ConfigureAwait(false);

            locked.ShouldNotBeNull();
            locked.State.ShouldBe(TaskState.Executing);
            locked.StartTime.ShouldNotBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetAndLock_NoPendingTasks_ReturnsNull()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            // First lock picks it up
            await provider.GetAndLockPendingTaskAsync().ConfigureAwait(false);

            // Second lock finds nothing
            var second = await provider.GetAndLockPendingTaskAsync().ConfigureAwait(false);
            second.ShouldBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetAndLock_FIFOOrder()
    {
        await Run<IServerTaskDataProvider, IRepository, IUnitOfWork>(async (provider, repository, unitOfWork) =>
        {
            // Create tasks with different queue times
            var task1 = new ServerTask
            {
                Name = "First Task",
                Description = "First",
                QueueTime = DateTimeOffset.UtcNow.AddMinutes(-10),
                State = TaskState.Pending,
                ServerTaskType = "Deploy",
                ProjectId = 1,
                EnvironmentId = 1,
                SpaceId = 1,
                LastModified = DateTimeOffset.UtcNow,
                BusinessProcessState = "Queued",
                StateOrder = 1,
                Weight = 1,
                DataVersion = Guid.NewGuid().ToByteArray()
            };

            var task2 = new ServerTask
            {
                Name = "Second Task",
                Description = "Second",
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
                DataVersion = Guid.NewGuid().ToByteArray()
            };

            await repository.InsertAsync(task1).ConfigureAwait(false);
            await repository.InsertAsync(task2).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var first = await provider.GetAndLockPendingTaskAsync().ConfigureAwait(false);
            first.ShouldNotBeNull();
            first.Name.ShouldBe("First Task");

            var second = await provider.GetAndLockPendingTaskAsync().ConfigureAwait(false);
            second.ShouldNotBeNull();
            second.Name.ShouldBe("Second Task");
        }).ConfigureAwait(false);
    }

    // ========== Full Lifecycle Integration ==========

    [Fact]
    public async Task FullLifecycle_HappyPath()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            // Pending
            var task = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            task.State.ShouldBe(TaskState.Pending);
            task.StartTime.ShouldBeNull();
            task.CompletedTime.ShouldBeNull();

            // Pending → Executing
            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing)
                .ConfigureAwait(false);
            task = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            task.State.ShouldBe(TaskState.Executing);
            task.CompletedTime.ShouldBeNull();

            // Executing → Success
            await provider.TransitionStateAsync(taskId, TaskState.Executing, TaskState.Success)
                .ConfigureAwait(false);
            task = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            task.State.ShouldBe(TaskState.Success);
            task.CompletedTime.ShouldNotBeNull();

            // Success → anything should fail
            await Should.ThrowAsync<InvalidStateTransitionException>(async () =>
                await provider.TransitionStateAsync(taskId, TaskState.Success, TaskState.Executing)
                    .ConfigureAwait(false)).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullLifecycle_FailurePath()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing)
                .ConfigureAwait(false);
            await provider.TransitionStateAsync(taskId, TaskState.Executing, TaskState.Failed)
                .ConfigureAwait(false);

            var task = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            task.State.ShouldBe(TaskState.Failed);
            task.CompletedTime.ShouldNotBeNull();

            // Failed is terminal
            await Should.ThrowAsync<InvalidStateTransitionException>(async () =>
                await provider.TransitionStateAsync(taskId, TaskState.Failed, TaskState.Pending)
                    .ConfigureAwait(false)).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullLifecycle_CancellationPath()
    {
        await Run<IServerTaskDataProvider>(async provider =>
        {
            var taskId = await CreatePendingTaskAsync().ConfigureAwait(false);

            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing)
                .ConfigureAwait(false);
            await provider.TransitionStateAsync(taskId, TaskState.Executing, TaskState.Cancelling)
                .ConfigureAwait(false);
            await provider.TransitionStateAsync(taskId, TaskState.Cancelling, TaskState.Cancelled)
                .ConfigureAwait(false);

            var task = await provider.GetServerTaskByIdAsync(taskId).ConfigureAwait(false);
            task.State.ShouldBe(TaskState.Cancelled);
            task.CompletedTime.ShouldNotBeNull();
        }).ConfigureAwait(false);
    }
}
