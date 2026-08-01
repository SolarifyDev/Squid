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

    [Fact]
    public async Task OnFailure_CleansUpCheckpoint()
    {
        var ctx = CreateContext();
        _serverTaskService.Setup(s => s.GetTaskAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskSummaryDto { Id = ctx.ServerTaskId, State = TaskState.Executing });

        await _sut.OnFailureAsync(ctx, new Exception("test error"), CancellationToken.None);

        _checkpointService.Verify(c => c.DeleteAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task OnPaused_AlreadyPaused_DoesNotTransitionState()
    {
        // The common case: manual intervention and guided failure set Paused themselves
        // immediately before throwing DeploymentSuspendedException. Paused → Paused is not a
        // legal edge, so re-writing it here would throw for them.
        var ctx = CreateContext();
        _serverTaskService.Setup(s => s.GetTaskAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskSummaryDto { Id = ctx.ServerTaskId, State = TaskState.Paused });

        await _sut.OnPausedAsync(ctx, CancellationToken.None);

        _serverTaskService.Verify(s => s.TransitionStateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(TaskState.Cancelling)]
    [InlineData(TaskState.Pending)]
    [InlineData(TaskState.Failed)]
    [InlineData(TaskState.Success)]
    public async Task OnPaused_NotExecuting_LeavesTheStateAlone(string currentState)
    {
        // Executing is the only legal source of a -> Paused edge. A racing
        // cancel (Cancelling) must win rather than be overwritten by a pause, and a terminal task
        // has already recorded its outcome. Transitioning blindly would throw.
        var ctx = CreateContext();
        _serverTaskService.Setup(s => s.GetTaskAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskSummaryDto { Id = ctx.ServerTaskId, State = currentState });

        await Should.NotThrowAsync(() => _sut.OnPausedAsync(ctx, CancellationToken.None));

        _serverTaskService.Verify(s => s.TransitionStateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── The guard is shared by all three pause outcomes ──────────────────

    /// <summary>Which pause outcome is being driven; all three share PauseIfStillExecutingAsync.</summary>
    public enum PauseOutcome { Suspended, TimedOut, Transient }

    private Task InvokePauseAsync(PauseOutcome outcome, DeploymentTaskContext ctx) => outcome switch
    {
        PauseOutcome.Suspended => _sut.OnPausedAsync(ctx, CancellationToken.None),
        PauseOutcome.TimedOut => _sut.OnTimedOutAsync(ctx, new Exception("timeout"), CancellationToken.None),
        PauseOutcome.Transient => _sut.OnTransientPauseAsync(ctx, new Exception("blip"), CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    [Theory]
    [InlineData(PauseOutcome.Suspended)]
    [InlineData(PauseOutcome.TimedOut)]
    [InlineData(PauseOutcome.Transient)]
    public async Task AnyPauseOutcome_RacedByACancel_LeavesTheCancelToWin(PauseOutcome outcome)
    {
        // What this pins: the pause does not overwrite a cancel that landed mid-unwind, and it
        // stops reaching for an illegal edge to discover that. Cancelling -> Paused is not legal,
        // so a blind transition threw and the runner's SafeCompleteAsync swallowed it.
        //
        // It does NOT pin that the task recovers. The row stays Cancelling either way — this
        // handler cannot free it, and nothing else does today (see PauseIfStillExecutingAsync).
        // The Times.Never below is the whole claim: no illegal transition is attempted.
        var ctx = CreateContext();
        _serverTaskService.Setup(s => s.GetTaskAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskSummaryDto { Id = ctx.ServerTaskId, State = TaskState.Cancelling });

        await Should.NotThrowAsync(() => InvokePauseAsync(outcome, ctx),
            customMessage: $"{outcome} must not attempt the illegal Cancelling -> Paused transition.");

        _serverTaskService.Verify(s => s.TransitionStateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(PauseOutcome.Suspended, TaskState.Paused)]
    [InlineData(PauseOutcome.Suspended, TaskState.Pending)]
    [InlineData(PauseOutcome.Suspended, TaskState.Success)]
    [InlineData(PauseOutcome.TimedOut, TaskState.Paused)]
    [InlineData(PauseOutcome.TimedOut, TaskState.Pending)]
    [InlineData(PauseOutcome.TimedOut, TaskState.Success)]
    [InlineData(PauseOutcome.Transient, TaskState.Paused)]
    [InlineData(PauseOutcome.Transient, TaskState.Pending)]
    [InlineData(PauseOutcome.Transient, TaskState.Success)]
    public async Task AnyPauseOutcome_NotExecuting_LeavesTheStateAlone(PauseOutcome outcome, string currentState)
    {
        var ctx = CreateContext();
        _serverTaskService.Setup(s => s.GetTaskAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskSummaryDto { Id = ctx.ServerTaskId, State = currentState });

        await Should.NotThrowAsync(() => InvokePauseAsync(outcome, ctx));

        _serverTaskService.Verify(s => s.TransitionStateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(PauseOutcome.Suspended)]
    [InlineData(PauseOutcome.TimedOut)]
    [InlineData(PauseOutcome.Transient)]
    public async Task AnyPauseOutcome_StillExecuting_TransitionsToPaused(PauseOutcome outcome)
    {
        // The positive half: guarding must not stop the pause happening on the normal path.
        var ctx = CreateContext();
        _serverTaskService.Setup(s => s.GetTaskAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskSummaryDto { Id = ctx.ServerTaskId, State = TaskState.Executing });

        await InvokePauseAsync(outcome, ctx);

        _serverTaskService.Verify(s => s.TransitionStateAsync(ctx.ServerTaskId, TaskState.Executing, TaskState.Paused, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnPaused_StillExecuting_TransitionsToPaused()
    {
        // A suspend site that runs BEFORE the task is claimed as Executing (the checkpoint
        // resume phase) has no from-state of its own to transition, so the outcome must be
        // written here. Leaving such a task Executing is worse than any pause: resume rejects a
        // non-Paused task, cancel only reaches Cancelling, and the row keeps occupying the
        // environment's concurrency slot — blocking every other deployment to that environment.
        var ctx = CreateContext();
        _serverTaskService.Setup(s => s.GetTaskAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskSummaryDto { Id = ctx.ServerTaskId, State = TaskState.Executing });

        await _sut.OnPausedAsync(ctx, CancellationToken.None);

        _serverTaskService.Verify(s => s.TransitionStateAsync(ctx.ServerTaskId, TaskState.Executing, TaskState.Paused, It.IsAny<CancellationToken>()), Times.Once);
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

    // ========== OnTimedOutAsync ==========
    // Timeout is treated as a resumable pause: transition to Paused, KEEP the
    // checkpoint (it's the resume point), and write NO completion record (the
    // deployment hasn't completed — it's suspended).

    [Fact]
    public async Task OnTimedOut_TransitionsExecutingToPaused()
    {
        var ctx = CreateContext();
        _serverTaskService.Setup(s => s.GetTaskAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskSummaryDto { Id = ctx.ServerTaskId, State = TaskState.Executing });

        await _sut.OnTimedOutAsync(ctx, new Exception("timed out"), CancellationToken.None);

        _serverTaskService.Verify(s => s.TransitionStateAsync(ctx.ServerTaskId, TaskState.Executing, TaskState.Paused, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnTimedOut_DoesNotCleanupCheckpoint()
    {
        // The headline behaviour: the checkpoint is the resume point. Deleting it
        // (as OnFailure does) would make the timed-out deployment unrecoverable —
        // exactly the regression this feature fixes.
        var ctx = CreateContext();
        _serverTaskService.Setup(s => s.GetTaskAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskSummaryDto { Id = ctx.ServerTaskId, State = TaskState.Executing });

        await _sut.OnTimedOutAsync(ctx, new Exception("timed out"), CancellationToken.None);

        _checkpointService.Verify(c => c.DeleteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnTimedOut_DoesNotRecordCompletion()
    {
        var ctx = CreateContext();
        _serverTaskService.Setup(s => s.GetTaskAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskSummaryDto { Id = ctx.ServerTaskId, State = TaskState.Executing });

        await _sut.OnTimedOutAsync(ctx, new Exception("timed out"), CancellationToken.None);

        _completionDataProvider.Verify(c => c.AddDeploymentCompletionAsync(It.IsAny<DeploymentCompletion>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnTimedOut_DoesNotTriggerAutoDeployments()
    {
        // Auto-deploy chaining only happens on a genuine success. A paused/timed-out
        // deployment hasn't produced a result, so nothing downstream should fire.
        var ctx = CreateContext();
        _serverTaskService.Setup(s => s.GetTaskAsync(ctx.ServerTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskSummaryDto { Id = ctx.ServerTaskId, State = TaskState.Executing });

        await _sut.OnTimedOutAsync(ctx, new Exception("timed out"), CancellationToken.None);

        _autoDeployService.Verify(a => a.TriggerAutoDeploymentsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
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
