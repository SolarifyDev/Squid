using System;
using System.Linq.Expressions;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Jobs;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.ServerTask;

/// <summary>
/// Manual resume of a paused deployment — the operator-facing trigger that
/// re-dispatches a deployment paused by the timeout-resumable path (or any other
/// pause without a pending interruption). Unlike <c>TryAutoResumeAsync</c>, which
/// silently no-ops, <c>ResumeTaskAsync</c> surfaces every precondition failure as
/// a typed exception so the API returns an actionable error instead of a
/// misleading 200.
/// </summary>
public class ServerTaskControlServiceResumeTests
{
    private readonly Mock<IServerTaskDataProvider> _dataProvider = new();
    private readonly Mock<IServerTaskService> _serverTaskService = new();
    private readonly Mock<ITaskCancellationRegistry> _registry = new();
    private readonly Mock<IDeploymentInterruptionService> _interruptionService = new();
    private readonly Mock<IDeploymentCheckpointService> _checkpointService = new();
    private readonly Mock<ISquidBackgroundJobClient> _jobClient = new();
    private readonly ServerTaskControlService _sut;

    public ServerTaskControlServiceResumeTests()
    {
        _sut = new ServerTaskControlService(_dataProvider.Object, _serverTaskService.Object, _registry.Object, _interruptionService.Object, _checkpointService.Object, _jobClient.Object);
    }

    private static Expression<Func<ISquidBackgroundJobClient, string>> AnyEnqueue() =>
        j => j.Enqueue(It.IsAny<Expression<Func<IDeploymentTaskExecutor, Task>>>(), It.IsAny<string>());

    [Fact]
    public async Task Resume_PausedNoPendingInterruptions_EnqueuesProcessAsync()
    {
        var task = new ServerTaskEntity { Id = 7, State = TaskState.Paused, HasPendingInterruptions = false };
        _dataProvider.Setup(d => d.GetServerTaskByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        _jobClient.Setup(AnyEnqueue()).Returns("resume-job-1");

        await _sut.ResumeTaskAsync(7, CancellationToken.None);

        _jobClient.Verify(AnyEnqueue(), Times.Once);
    }

    [Fact]
    public async Task Resume_EnqueueReturnsJobId_PersistsNewJobId()
    {
        // The new Hangfire job id must be written back so a subsequent cancel can
        // delete the right job. Mirrors TryAutoResume's persistence.
        var task = new ServerTaskEntity { Id = 7, State = TaskState.Paused, HasPendingInterruptions = false };
        _dataProvider.Setup(d => d.GetServerTaskByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        _jobClient.Setup(AnyEnqueue()).Returns("resume-job-1");

        await _sut.ResumeTaskAsync(7, CancellationToken.None);

        task.JobId.ShouldBe("resume-job-1");
        _dataProvider.Verify(d => d.UpdateServerTaskStateAsync(7, TaskState.Paused, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Resume_NotFound_ThrowsNotFound()
    {
        _dataProvider.Setup(d => d.GetServerTaskByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((ServerTaskEntity)null);

        await Should.ThrowAsync<ServerTaskNotFoundException>(() => _sut.ResumeTaskAsync(99, CancellationToken.None));

        _jobClient.Verify(AnyEnqueue(), Times.Never);
    }

    [Theory]
    [InlineData(TaskState.Executing)]
    [InlineData(TaskState.Pending)]
    [InlineData(TaskState.Cancelling)]
    [InlineData(TaskState.Success)]
    [InlineData(TaskState.Failed)]
    [InlineData(TaskState.Cancelled)]
    [InlineData(TaskState.TimedOut)]
    public async Task Resume_NotPaused_ThrowsStateTransition(string state)
    {
        // Only a Paused task can be resumed (Paused→Executing is the lone valid
        // transition). Every other state is rejected up-front, never enqueued.
        var task = new ServerTaskEntity { Id = 7, State = state };
        _dataProvider.Setup(d => d.GetServerTaskByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await Should.ThrowAsync<ServerTaskStateTransitionException>(() => _sut.ResumeTaskAsync(7, CancellationToken.None));

        _jobClient.Verify(AnyEnqueue(), Times.Never);
    }

    [Fact]
    public async Task Resume_PausedWithPendingInterruption_ThrowsAwaitingInterruption()
    {
        // A task paused for a guided-failure / manual-intervention prompt must be
        // resumed by SUBMITTING the interruption, not by a blind re-dispatch.
        var task = new ServerTaskEntity { Id = 7, State = TaskState.Paused, HasPendingInterruptions = true };
        _dataProvider.Setup(d => d.GetServerTaskByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        await Should.ThrowAsync<ServerTaskAwaitingInterruptionException>(() => _sut.ResumeTaskAsync(7, CancellationToken.None));

        _jobClient.Verify(AnyEnqueue(), Times.Never);
    }

    [Fact]
    public async Task TryAutoResume_StillEnqueues_AfterSharedEnqueueRefactor()
    {
        // Regression: ResumeTaskAsync + TryAutoResumeAsync now share EnqueueResumeAsync.
        // Prove the auto path still dispatches for an eligible paused task.
        var task = new ServerTaskEntity { Id = 7, State = TaskState.Paused, HasPendingInterruptions = false };
        _dataProvider.Setup(d => d.GetServerTaskByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        _jobClient.Setup(AnyEnqueue()).Returns("auto-job-1");

        await _sut.TryAutoResumeAsync(7, CancellationToken.None);

        _jobClient.Verify(AnyEnqueue(), Times.Once);
        task.JobId.ShouldBe("auto-job-1");
    }
}
