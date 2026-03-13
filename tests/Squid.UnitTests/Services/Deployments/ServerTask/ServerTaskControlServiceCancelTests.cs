using System;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.ServerTask;

public class ServerTaskControlServiceCancelTests
{
    private readonly Mock<IServerTaskDataProvider> _dataProvider = new();
    private readonly Mock<IServerTaskService> _serverTaskService = new();
    private readonly Mock<ITaskCancellationRegistry> _registry = new();
    private readonly Mock<IDeploymentInterruptionService> _interruptionService = new();
    private readonly Mock<IDeploymentCheckpointService> _checkpointService = new();
    private readonly Mock<ISquidBackgroundJobClient> _jobClient = new();
    private readonly ServerTaskControlService _sut;

    public ServerTaskControlServiceCancelTests()
    {
        _sut = new ServerTaskControlService(_dataProvider.Object, _serverTaskService.Object, _registry.Object, _interruptionService.Object, _checkpointService.Object, _jobClient.Object);
    }

    [Fact]
    public async Task CancelExecuting_TransitionsToCancelling()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Executing };
        _dataProvider.Setup(d => d.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await _sut.CancelTaskAsync(1, CancellationToken.None);

        _serverTaskService.Verify(s => s.TransitionStateAsync(1, TaskState.Executing, TaskState.Cancelling, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelExecuting_SignalsCancellationRegistry()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Executing };
        _dataProvider.Setup(d => d.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await _sut.CancelTaskAsync(1, CancellationToken.None);

        _registry.Verify(r => r.TryCancel(1), Times.Once);
    }

    [Fact]
    public async Task CancelExecuting_WithJobId_DeletesHangfireJob()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Executing, JobId = "hangfire-job-1" };
        _dataProvider.Setup(d => d.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await _sut.CancelTaskAsync(1, CancellationToken.None);

        _jobClient.Verify(j => j.DeleteJob("hangfire-job-1"), Times.Once);
    }

    [Fact]
    public async Task CancelExecuting_WithoutJobId_DoesNotDeleteJob()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Executing, JobId = null };
        _dataProvider.Setup(d => d.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await _sut.CancelTaskAsync(1, CancellationToken.None);

        _jobClient.Verify(j => j.DeleteJob(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CancelPaused_TransitionsDirectlyToCancelled()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Paused };
        _dataProvider.Setup(d => d.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await _sut.CancelTaskAsync(1, CancellationToken.None);

        _serverTaskService.Verify(s => s.TransitionStateAsync(1, TaskState.Paused, TaskState.Cancelled, It.IsAny<CancellationToken>()), Times.Once);
        _interruptionService.Verify(i => i.CancelPendingInterruptionsAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelPending_TransitionsDirectlyToCancelled()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Pending, JobId = "job-1" };
        _dataProvider.Setup(d => d.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await _sut.CancelTaskAsync(1, CancellationToken.None);

        _serverTaskService.Verify(s => s.TransitionStateAsync(1, TaskState.Pending, TaskState.Cancelled, It.IsAny<CancellationToken>()), Times.Once);
        _jobClient.Verify(j => j.DeleteJob("job-1"), Times.Once);
    }

    [Fact]
    public async Task CancelAlreadyCancelling_IsIdempotent()
    {
        var task = new ServerTaskEntity { Id = 1, State = TaskState.Cancelling };
        _dataProvider.Setup(d => d.GetServerTaskByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await _sut.CancelTaskAsync(1, CancellationToken.None);

        _serverTaskService.Verify(s => s.TransitionStateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
