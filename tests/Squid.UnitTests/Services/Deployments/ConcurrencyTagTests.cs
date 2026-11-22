using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Squid.Core.Persistence.EntityConfigurations;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Jobs;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments;

/// <summary>
/// Pins the runner's environment concurrency-slot behaviour. The legacy in-process poll
/// ("wait up to 300s, then proceed anyway") is replaced by a single read-only free-slot check:
/// if the slot is free the deployment runs; if it is busy the still-Pending task is re-enqueued
/// (no worker held, never run-anyway), and a TOCTOU claim race (LoadTaskPhase's atomic
/// →Executing transition rejected by the unique index) surfaces as
/// <see cref="ConcurrencySlotOccupiedException"/> and is likewise re-enqueued, never failed.
/// The DB unique index is the hard cross-pod guarantee (covered by integration/E2E tiers).
/// </summary>
public class ConcurrencyTagTests
{
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly TaskCancellationRegistry _registry = new();
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();
    private readonly Mock<ISquidBackgroundJobClient> _backgroundJobClient = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task UntaggedTask_SkipsSlotCheck_AndRuns(string tag)
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskEntity { Id = 1, ConcurrencyTag = tag });

        var runner = CreateRunner();

        await runner.ProcessAsync(1, CancellationToken.None);

        _taskDataProvider.Verify(p => p.HasActiveTaskWithTagAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNotRequeued();
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TaskNotFound_SkipsSlotCheck_AndRuns()
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ServerTaskEntity)null);

        var runner = CreateRunner();

        await runner.ProcessAsync(99, CancellationToken.None);

        _taskDataProvider.Verify(p => p.HasActiveTaskWithTagAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNotRequeued();
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SlotFree_RunsDeployment_NoRequeue()
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskEntity { Id = 1, ConcurrencyTag = "deploy:env-5" });
        _taskDataProvider.Setup(p => p.HasActiveTaskWithTagAsync("deploy:env-5", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var runner = CreateRunner();

        await runner.ProcessAsync(1, CancellationToken.None);

        _taskDataProvider.Verify(p => p.HasActiveTaskWithTagAsync("deploy:env-5", 1, It.IsAny<CancellationToken>()), Times.Once);
        VerifyNotRequeued();
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SlotBusy_RequeuesAndPersistsNewJobId_AndDoesNotRunOrComplete()
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskEntity { Id = 1, ConcurrencyTag = "deploy:env-5" });
        _taskDataProvider.Setup(p => p.HasActiveTaskWithTagAsync("deploy:env-5", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _backgroundJobClient.Setup(c => c.Schedule<IDeploymentTaskExecutor>(
                It.IsAny<Expression<Func<IDeploymentTaskExecutor, Task>>>(), It.IsAny<TimeSpan>(), It.IsAny<string>()))
            .Returns("requeue-job-123");

        var runner = CreateRunner();

        await runner.ProcessAsync(1, CancellationToken.None);

        VerifyRequeuedOnce();
        _taskDataProvider.Verify(p => p.SetJobIdAsync(1, "requeue-job-123", It.IsAny<CancellationToken>()), Times.Once);
        _lifecycle.Verify(l => l.Initialize(It.IsAny<DeploymentTaskContext>()), Times.Never);
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AlreadyTerminal_ShortCircuits_NoRunNoRequeue()
    {
        // A re-dispatched job whose task was Cancelled while it sat re-enqueued must short-circuit,
        // not attempt an illegal Cancelled→Executing claim nor re-enqueue itself forever.
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskEntity { Id = 1, ConcurrencyTag = "deploy:env-5", State = TaskState.Cancelled });

        var runner = CreateRunner();

        await runner.ProcessAsync(1, CancellationToken.None);

        _taskDataProvider.Verify(p => p.HasActiveTaskWithTagAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNotRequeued();
        _lifecycle.Verify(l => l.Initialize(It.IsAny<DeploymentTaskContext>()), Times.Never);
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SlotClaimRace_RequeuesInsteadOfFailing()
    {
        // The free-slot check passes, but the claim (LoadTaskPhase's atomic →Executing) loses the
        // race to a peer pod — the unique index throws ConcurrencySlotOccupiedException. The runner
        // must re-enqueue (Pending, retried), NOT fail the task.
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskEntity { Id = 1, ConcurrencyTag = "deploy:env-5" });
        _taskDataProvider.Setup(p => p.HasActiveTaskWithTagAsync("deploy:env-5", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var claimRacePhase = new Mock<IDeploymentPipelinePhase>();
        claimRacePhase.SetupGet(p => p.Order).Returns(100);
        claimRacePhase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConcurrencySlotOccupiedException("deploy:env-5"));

        var runner = CreateRunner(claimRacePhase.Object);

        await runner.ProcessAsync(1, CancellationToken.None);

        VerifyRequeuedOnce();
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void OneActivePerTagIndexName_IsPinned()
    {
        // SSOT for the unique partial index: the migration SQL hard-codes this literal and the
        // data provider matches the 23505 ConstraintName against this const to map the violation
        // to ConcurrencySlotOccupiedException. A rename must change all three together — pin it so
        // the rename is a visible, deliberate decision rather than a silent mapping break.
        ServerTaskConfiguration.OneActivePerTagIndexName.ShouldBe("ux_server_task_active_per_tag");
    }

    private void VerifyRequeuedOnce() =>
        _backgroundJobClient.Verify(c => c.Schedule<IDeploymentTaskExecutor>(
            It.IsAny<Expression<Func<IDeploymentTaskExecutor, Task>>>(), It.IsAny<TimeSpan>(), It.IsAny<string>()), Times.Once);

    private void VerifyNotRequeued() =>
        _backgroundJobClient.Verify(c => c.Schedule<IDeploymentTaskExecutor>(
            It.IsAny<Expression<Func<IDeploymentTaskExecutor, Task>>>(), It.IsAny<TimeSpan>(), It.IsAny<string>()), Times.Never);

    private DeploymentPipelineRunner CreateRunner(params IDeploymentPipelinePhase[] phases)
    {
        return new DeploymentPipelineRunner(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object, _backgroundJobClient.Object);
    }
}
