using System;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Models.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentCompletionHandlerTests
{
    private readonly Mock<IGenericDataProvider> _genericDataProvider = new();
    private readonly Mock<IServerTaskService> _serverTaskService = new();
    private readonly Mock<IDeploymentDataProvider> _deploymentDataProvider = new();
    private readonly Mock<IDeploymentCompletionDataProvider> _completionDataProvider = new();
    private readonly Mock<IAutoDeployService> _autoDeployService = new();
    private readonly Mock<IDeploymentCheckpointService> _checkpointService = new();
    private readonly DeploymentCompletionHandler _sut;

    public DeploymentCompletionHandlerTests()
    {
        _genericDataProvider.Setup(g => g.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>(async (action, ct) => await action(ct));

        _deploymentDataProvider.Setup(d => d.GetDeploymentByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Deployment { Id = 1, SpaceId = 1 });

        _sut = new DeploymentCompletionHandler(_genericDataProvider.Object, _serverTaskService.Object, _deploymentDataProvider.Object, _completionDataProvider.Object, _autoDeployService.Object, _checkpointService.Object);
    }

    // ========== OnFailureAsync ==========

    [Theory]
    [InlineData(TaskState.Executing)]
    [InlineData(TaskState.Cancelling)]
    public async Task OnFailure_TransitionsFromCurrentStateToFailed(string currentState)
    {
        var ctx = CreateContext();
        _serverTaskService.Setup(s => s.GetTaskAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskSummaryDto { Id = ctx.ServerTaskId, State = currentState });

        await _sut.OnFailureAsync(ctx, new Exception("test error"), CancellationToken.None);

        _serverTaskService.Verify(s => s.TransitionStateAsync(ctx.ServerTaskId, currentState, TaskState.Failed, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnFailure_RecordsCompletionAsFailed()
    {
        var ctx = CreateContext();
        _serverTaskService.Setup(s => s.GetTaskAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskSummaryDto { Id = ctx.ServerTaskId, State = TaskState.Executing });

        await _sut.OnFailureAsync(ctx, new Exception("test error"), CancellationToken.None);

        _completionDataProvider.Verify(c => c.AddDeploymentCompletionAsync(It.Is<DeploymentCompletion>(dc => dc.State == TaskState.Failed), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== OnCancelledAsync ==========

    [Fact]
    public async Task OnCancelled_TransitionsCancellingToCancelled()
    {
        var ctx = CreateContext();

        await _sut.OnCancelledAsync(ctx, CancellationToken.None);

        _serverTaskService.Verify(s => s.TransitionStateAsync(ctx.ServerTaskId, TaskState.Cancelling, TaskState.Cancelled, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnCancelled_RecordsCompletionAsFailed()
    {
        var ctx = CreateContext();

        await _sut.OnCancelledAsync(ctx, CancellationToken.None);

        _completionDataProvider.Verify(c => c.AddDeploymentCompletionAsync(It.Is<DeploymentCompletion>(dc => dc.State == TaskState.Failed), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnCancelled_CleansUpCheckpoint()
    {
        var ctx = CreateContext();

        await _sut.OnCancelledAsync(ctx, CancellationToken.None);

        _checkpointService.Verify(c => c.DeleteAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== OnPausedAsync ==========

    [Fact]
    public async Task OnPaused_DoesNotTransitionState()
    {
        var ctx = CreateContext();

        await _sut.OnPausedAsync(ctx, CancellationToken.None);

        _serverTaskService.Verify(s => s.TransitionStateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnPaused_DoesNotCleanupCheckpoint()
    {
        var ctx = CreateContext();

        await _sut.OnPausedAsync(ctx, CancellationToken.None);

        _checkpointService.Verify(c => c.DeleteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnPaused_DoesNotRecordCompletion()
    {
        var ctx = CreateContext();

        await _sut.OnPausedAsync(ctx, CancellationToken.None);

        _completionDataProvider.Verify(c => c.AddDeploymentCompletionAsync(It.IsAny<DeploymentCompletion>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ========== Helpers ==========

    private static DeploymentTaskContext CreateContext()
    {
        return new DeploymentTaskContext
        {
            ServerTaskId = 1,
            Deployment = new Deployment { Id = 1, SpaceId = 1 }
        };
    }
}
