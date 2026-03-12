using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Jobs;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Cancellation;

public class ServerTaskControlServiceTests
{
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();
    private readonly Mock<IServerTaskService> _taskService = new();
    private readonly Mock<ITaskCancellationRegistry> _cancellationRegistry = new();
    private readonly Mock<IDeploymentInterruptionService> _interruptionService = new();
    private readonly Mock<IDeploymentCheckpointService> _checkpointService = new();
    private readonly Mock<ISquidBackgroundJobClient> _jobClient = new();
    private readonly ServerTaskControlService _sut;

    public ServerTaskControlServiceTests()
    {
        _sut = new ServerTaskControlService(_taskDataProvider.Object, _taskService.Object, _cancellationRegistry.Object, _interruptionService.Object, _checkpointService.Object, _jobClient.Object);
    }

    [Fact]
    public async Task CancelTask_Pending_TransitionsToCancelledAndDeletesJob()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Pending, JobId = "hangfire-123" };
        _taskDataProvider.Setup(p => p.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await _sut.CancelTaskAsync(1);

        _taskService.Verify(s => s.TransitionStateAsync(1, TaskState.Pending, TaskState.Cancelled, It.IsAny<CancellationToken>()), Times.Once);
        _jobClient.Verify(j => j.DeleteJob("hangfire-123"), Times.Once);
    }

    [Fact]
    public async Task CancelTask_Executing_TransitionsToCancellingAndSignalsCts()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Executing };
        _taskDataProvider.Setup(p => p.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await _sut.CancelTaskAsync(1);

        _taskService.Verify(s => s.TransitionStateAsync(1, TaskState.Executing, TaskState.Cancelling, It.IsAny<CancellationToken>()), Times.Once);
        _cancellationRegistry.Verify(r => r.TryCancel(1), Times.Once);
    }

    [Fact]
    public async Task CancelTask_Paused_TransitionsToCancelledAndCleansUp()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Paused };
        _taskDataProvider.Setup(p => p.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await _sut.CancelTaskAsync(1);

        _taskService.Verify(s => s.TransitionStateAsync(1, TaskState.Paused, TaskState.Cancelled, It.IsAny<CancellationToken>()), Times.Once);
        _interruptionService.Verify(s => s.CancelPendingInterruptionsAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        _checkpointService.Verify(s => s.DeleteAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelTask_Terminal_ThrowsStateTransitionException()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Success };
        _taskDataProvider.Setup(p => p.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await Should.ThrowAsync<ServerTaskStateTransitionException>(() => _sut.CancelTaskAsync(1));
    }

    [Fact]
    public async Task CancelTask_Cancelling_IsIdempotent()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Cancelling };
        _taskDataProvider.Setup(p => p.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await _sut.CancelTaskAsync(1);

        _taskService.Verify(s => s.TransitionStateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryAutoResume_PausedAndNoPending_Enqueues()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Paused, HasPendingInterruptions = false };
        _taskDataProvider.Setup(p => p.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        _jobClient.Setup(j => j.Enqueue(It.IsAny<System.Linq.Expressions.Expression<System.Func<IDeploymentTaskExecutor, System.Threading.Tasks.Task>>>(), It.IsAny<string>())).Returns("new-job");

        await _sut.TryAutoResumeAsync(1);

        _jobClient.Verify(j => j.Enqueue(It.IsAny<System.Linq.Expressions.Expression<System.Func<IDeploymentTaskExecutor, System.Threading.Tasks.Task>>>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task TryAutoResume_NotPaused_NoOp()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Executing };
        _taskDataProvider.Setup(p => p.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await _sut.TryAutoResumeAsync(1);

        _jobClient.Verify(j => j.Enqueue(It.IsAny<System.Linq.Expressions.Expression<System.Func<IDeploymentTaskExecutor, System.Threading.Tasks.Task>>>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TryAutoResume_PausedWithPendingInterruptions_NoOp()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Paused, HasPendingInterruptions = true };
        _taskDataProvider.Setup(p => p.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await _sut.TryAutoResumeAsync(1);

        _jobClient.Verify(j => j.Enqueue(It.IsAny<System.Linq.Expressions.Expression<System.Func<IDeploymentTaskExecutor, System.Threading.Tasks.Task>>>(), It.IsAny<string>()), Times.Never);
    }
}
