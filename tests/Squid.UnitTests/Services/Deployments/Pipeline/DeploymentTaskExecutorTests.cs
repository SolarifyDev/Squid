using System.Linq.Expressions;
using Moq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Jobs;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentTaskExecutorTests
{
    private readonly Mock<IDeploymentPipelinePhase> _phase = new();
    private readonly Mock<IServerTaskDataProvider> _tasks = new();
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly Mock<ISquidBackgroundJobClient> _jobs = new();

    [Fact]
    public async Task RepeatedInitialDelivery_DoesNotResumePausedTask()
    {
        var task = TaskWithState(TaskState.Paused);
        var executor = CreateExecutor(task);

        await executor.ProcessAsync(task.Id, CancellationToken.None, false);

        _phase.Verify(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _tasks.Verify(t => t.RunWithTriggerTaskLockAsync(task.Id, false, It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DefaultDelivery_ResumesOnlyHostShutdownPauses(bool isTriggerTask, bool resumeOnRedelivery)
    {
        var task = TaskWithState(TaskState.Paused);
        task.IsTriggerTask = isTriggerTask;
        task.ResumeOnRedelivery = resumeOnRedelivery;
        var executor = CreateExecutor(task);

        await executor.ProcessAsync(task.Id, CancellationToken.None);

        _phase.Verify(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()),
            resumeOnRedelivery ? Times.Once() : Times.Never());
        if (!resumeOnRedelivery) _completion.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitResume_RunsPausedTask(bool isTriggerTask)
    {
        var task = TaskWithState(TaskState.Paused);
        task.IsTriggerTask = isTriggerTask;
        var executor = CreateExecutor(task);

        await executor.ProcessAsync(task.Id, CancellationToken.None, true);

        _phase.Verify(p => p.ExecuteAsync(It.Is<DeploymentTaskContext>(ctx => ctx.ServerTaskId == task.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExplicitResume_WaitsForPreviousOwnerAndRunsPipeline()
    {
        var task = TaskWithState(TaskState.Paused);
        var executor = CreateExecutor(task);

        await executor.ProcessAsync(task.Id, CancellationToken.None, true);

        _tasks.Verify(t => t.RunWithTriggerTaskLockAsync(task.Id, true, It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()), Times.Once);
        _phase.Verify(p => p.ExecuteAsync(It.Is<DeploymentTaskContext>(ctx => ctx.ServerTaskId == task.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExplicitResume_AfterAnotherPause_DoesNotResumeTheNewPause()
    {
        var task = TaskWithState(TaskState.Paused);
        var executor = CreateExecutor(task);
        _tasks.SetupSequence(t => t.GetServerTaskByIdNoTrackingAsync(task.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(task)
            .ReturnsAsync(new ServerTaskEntity { Id = task.Id, IsTriggerTask = true, State = TaskState.Paused, DataVersion = [2] });

        await executor.ProcessAsync(task.Id, CancellationToken.None, true);

        _phase.Verify(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InitialDelivery_WhenAnotherOwnerHoldsTheLock_DoesNotEnterPipelineOrComplete()
    {
        var task = TaskWithState(TaskState.Executing);
        var executor = CreateExecutor(task);
        _tasks.Setup(t => t.RunWithTriggerTaskLockAsync(task.Id, false, It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await executor.ProcessAsync(task.Id, CancellationToken.None, false);

        _phase.Verify(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingWithoutOwner_CompletesCancellationBeforeAnyPhase(bool resume)
    {
        var task = TaskWithState(TaskState.Cancelling);
        var executor = CreateExecutor(task);
        string claimedOwner = null;
        _tasks.Setup(t => t.TryClaimUnleasedCancellationAsync(task.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, string owner, CancellationToken _) =>
            {
                claimedOwner = owner;
                return true;
            });

        await executor.ProcessAsync(task.Id, CancellationToken.None, resume);

        claimedOwner.ShouldNotBeNullOrEmpty();
        _completion.Verify(c => c.OnCancelledAsync(It.Is<DeploymentTaskContext>(ctx => ctx.ServerTaskId == task.Id && ctx.LeaseOwner == claimedOwner), It.IsAny<CancellationToken>()), Times.Once);
        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentCancelledEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _phase.Verify(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Redelivery_WhileAnotherOwnerIsCancelling_LeavesCompletionToOwner()
    {
        var task = TaskWithState(TaskState.Cancelling);
        task.LeaseOwner = "running-worker/run";
        task.LeaseRenewedAt = DateTimeOffset.UtcNow;
        var executor = CreateExecutor(task);
        _tasks.Setup(t => t.TryClaimUnleasedCancellationAsync(task.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await executor.ProcessAsync(task.Id, CancellationToken.None);

        _phase.Verify(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.VerifyNoOtherCalls();
        _lifecycle.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ManualDeployment_UsesExistingPipelineWithoutTriggerOwnership()
    {
        var task = TaskWithState(TaskState.Pending);
        task.IsTriggerTask = false;
        var executor = CreateExecutor(task);

        await executor.ProcessAsync(task.Id, CancellationToken.None);

        _phase.Verify(p => p.ExecuteAsync(It.Is<DeploymentTaskContext>(ctx => ctx.ServerTaskId == task.Id), It.IsAny<CancellationToken>()), Times.Once);
        _tasks.Verify(t => t.RunWithTriggerTaskLockAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task DeferredDelivery_PreservesResumeFlag(bool resume, bool slotLostDuringClaim, bool isTriggerTask)
    {
        var task = TaskWithState(resume ? TaskState.Paused : TaskState.Pending);
        task.IsTriggerTask = isTriggerTask;
        task.ConcurrencyTag = "environment-1";
        var executor = CreateExecutor(task);
        var scheduled = CaptureDeferredDelivery();
        _tasks.Setup(t => t.HasActiveTaskWithTagAsync(task.ConcurrencyTag, task.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(!slotLostDuringClaim);
        if (slotLostDuringClaim)
            _phase.Setup(p => p.ExecuteAsync(It.Is<DeploymentTaskContext>(ctx => ctx.ServerTaskId == task.Id), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ConcurrencySlotOccupiedException(task.ConcurrencyTag));

        await executor.ProcessAsync(task.Id, CancellationToken.None, resume);

        var delivery = scheduled();
        delivery.ShouldNotBeNull();
        var call = (MethodCallExpression)delivery.Body;

        call.Method.Name.ShouldBe(nameof(IDeploymentTaskExecutor.ProcessAsync));
        call.Arguments.Count.ShouldBe(3);
        Expression.Lambda<Func<bool>>(call.Arguments[2]).Compile()().ShouldBe(resume);
        _tasks.Verify(t => t.SetJobIdAsync(task.Id, "slot-job", It.IsAny<CancellationToken>()), Times.Once);
        task.State = TaskState.Paused;
        _tasks.Setup(t => t.HasActiveTaskWithTagAsync(task.ConcurrencyTag, task.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _phase.Setup(p => p.ExecuteAsync(It.Is<DeploymentTaskContext>(ctx => ctx.ServerTaskId == task.Id), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _phase.Invocations.Clear();

        await delivery.Compile()(executor);

        _phase.Verify(p => p.ExecuteAsync(It.Is<DeploymentTaskContext>(ctx => ctx.ServerTaskId == task.Id), It.IsAny<CancellationToken>()), resume ? Times.Once() : Times.Never());
    }

    private Func<Expression<Func<IDeploymentTaskExecutor, Task>>> CaptureDeferredDelivery()
    {
        Expression<Func<IDeploymentTaskExecutor, Task>> scheduled = null;
        _jobs.Setup(j => j.Schedule(It.IsAny<Expression<Func<IDeploymentTaskExecutor, Task>>>(),
                It.IsAny<TimeSpan>(), It.IsAny<string>()))
            .Callback<Expression<Func<IDeploymentTaskExecutor, Task>>, TimeSpan, string>((e, _, _) => scheduled = e)
            .Returns("slot-job");

        return () => scheduled;
    }

    private DeploymentPipelineRunner CreateExecutor(ServerTaskEntity task)
    {
        _phase.SetupGet(p => p.Order).Returns(100);
        _phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tasks.Setup(t => t.GetServerTaskByIdNoTrackingAsync(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        _tasks.Setup(t => t.RunWithTriggerTaskLockAsync(task.Id, It.IsAny<bool>(), It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns<int, bool, Func<Task>, CancellationToken>((_, _, run, _) => run());

        return new DeploymentPipelineRunner(new[] { _phase.Object }, _lifecycle.Object, _completion.Object,
            new TaskCancellationRegistry(), _tasks.Object, _jobs.Object);
    }

    private static ServerTaskEntity TaskWithState(string state)
    {
        return new ServerTaskEntity { Id = 1, State = state, IsTriggerTask = true, DataVersion = [1] };
    }
}
