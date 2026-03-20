using System;
using System.Diagnostics;
using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments;

public class ConcurrencyTagTests
{
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly TaskCancellationRegistry _registry = new();
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WaitForConcurrencySlot_NullOrEmptyTag_SkipsWait(string tag)
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskEntity { Id = 1, ConcurrencyTag = tag });

        var runner = CreateRunner();

        await runner.ProcessAsync(1, CancellationToken.None);

        _taskDataProvider.Verify(p => p.HasExecutingTaskWithTagAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitForConcurrencySlot_TaskNotFound_SkipsWait()
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ServerTaskEntity)null);

        var runner = CreateRunner();

        await runner.ProcessAsync(99, CancellationToken.None);

        _taskDataProvider.Verify(p => p.HasExecutingTaskWithTagAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitForConcurrencySlot_NoBlocker_Immediate()
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskEntity { Id = 1, ConcurrencyTag = "deploy:env-5" });
        _taskDataProvider.Setup(p => p.HasExecutingTaskWithTagAsync("deploy:env-5", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var runner = CreateRunner();

        await runner.ProcessAsync(1, CancellationToken.None);

        _taskDataProvider.Verify(p => p.HasExecutingTaskWithTagAsync("deploy:env-5", 1, It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitForConcurrencySlot_BlockerCompletes_Proceeds()
    {
        var callCount = 0;

        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskEntity { Id = 1, ConcurrencyTag = "deploy:env-5" });
        _taskDataProvider.Setup(p => p.HasExecutingTaskWithTagAsync("deploy:env-5", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount <= 2;
            });

        var runner = CreateRunner();

        await runner.ProcessAsync(1, CancellationToken.None);

        callCount.ShouldBe(3);
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitForConcurrencySlot_TimeoutExceeded_ProceedsAnyway()
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskEntity { Id = 1, ConcurrencyTag = "deploy:env-5" });
        _taskDataProvider.Setup(p => p.HasExecutingTaskWithTagAsync("deploy:env-5", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var runner = CreateRunnerWithFastConcurrency();

        var sw = Stopwatch.StartNew();
        await runner.ProcessAsync(1, CancellationToken.None);
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitForConcurrencySlot_CancellationDuringWait_PropagatesCancellation()
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskEntity { Id = 1, ConcurrencyTag = "deploy:env-5" });
        _taskDataProvider.Setup(p => p.HasExecutingTaskWithTagAsync("deploy:env-5", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var runner = CreateRunnerWithFastConcurrency(concurrencyMaxWait: TimeSpan.FromSeconds(30));

        await runner.ProcessAsync(1, cts.Token);

        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WaitForConcurrencySlot_RegistryCancelDuringWait_PropagatesCancellation()
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskEntity { Id = 1, ConcurrencyTag = "deploy:env-5" });
        _taskDataProvider.Setup(p => p.HasExecutingTaskWithTagAsync("deploy:env-5", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                _registry.TryCancel(1);
                return true;
            });

        var runner = CreateRunnerWithFastConcurrency(concurrencyMaxWait: TimeSpan.FromSeconds(30));

        await runner.ProcessAsync(1, CancellationToken.None);

        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WaitForConcurrencySlot_ConcurrencyWaitCountsTowardDeploymentTimeout()
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskEntity { Id = 1, ConcurrencyTag = "deploy:env-5" });
        _taskDataProvider.Setup(p => p.HasExecutingTaskWithTagAsync("deploy:env-5", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var runner = new DeploymentPipelineRunner(Enumerable.Empty<IDeploymentPipelinePhase>(), _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object)
        {
            DeploymentTimeout = TimeSpan.FromMilliseconds(100),
            ConcurrencyMaxWait = TimeSpan.FromSeconds(60),
            ConcurrencyPollInterval = TimeSpan.FromMilliseconds(10)
        };

        var sw = Stopwatch.StartNew();
        await runner.ProcessAsync(1, CancellationToken.None);
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    private DeploymentPipelineRunner CreateRunner(params IDeploymentPipelinePhase[] phases)
    {
        return new DeploymentPipelineRunner(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object);
    }

    private DeploymentPipelineRunner CreateRunnerWithFastConcurrency(TimeSpan? concurrencyMaxWait = null, params IDeploymentPipelinePhase[] phases)
    {
        return new DeploymentPipelineRunner(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object)
        {
            ConcurrencyMaxWait = concurrencyMaxWait ?? TimeSpan.FromMilliseconds(100),
            ConcurrencyPollInterval = TimeSpan.FromMilliseconds(10)
        };
    }
}
