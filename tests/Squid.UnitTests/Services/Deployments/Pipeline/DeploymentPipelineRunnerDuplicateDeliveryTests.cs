using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Core.Services.Jobs;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// A duplicate delivery on the SAME pod (double resume, manual resume + auto-resume) shares the process-wide registry with the live run. The claim
/// fences the duplicate, but the registry must never let it dispose or remove the live run's cancellation source: the live run would later throw
/// ObjectDisposedException (recorded Failed) and a user cancel / the cross-pod cancel watcher would silently reach nothing.
/// </summary>
public class DeploymentPipelineRunnerDuplicateDeliveryTests
{
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly TaskCancellationRegistry _registry = new();

    [Fact]
    public async Task FencedDuplicate_DoesNotBreakTheLiveRun_AndCancelStillReachesIt()
    {
        var liveInPhase = new TaskCompletionSource();
        var releaseLive = new TaskCompletionSource();
        var cancelReachedLive = false;

        var live = Runner(ClaimPhase(), WorkPhase(async (_, ct) =>
        {
            liveInPhase.SetResult();
            await releaseLive.Task;
            cancelReachedLive = ct.IsCancellationRequested;
        }));
        var duplicate = Runner(PlainPhase((_, _) => throw new ServerTaskOwnedByLiveProcessException(42, "pod-a:1:x")));

        var liveRun = live.ProcessAsync(42, CancellationToken.None);
        await liveInPhase.Task;

        await duplicate.ProcessAsync(42, CancellationToken.None);

        _registry.TryCancel(42).ShouldBeTrue("the duplicate's cleanup must not have removed the live run's registration");
        releaseLive.SetResult();
        await liveRun;

        cancelReachedLive.ShouldBeTrue();
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FencedDuplicate_LiveRunStillEndsSuccess()
    {
        var liveInPhase = new TaskCompletionSource();
        var releaseLive = new TaskCompletionSource();

        var live = Runner(ClaimPhase(), WorkPhase(async (_, ct) =>
        {
            liveInPhase.SetResult();
            await releaseLive.Task;
            ct.ThrowIfCancellationRequested();
        }));
        var duplicate = Runner(PlainPhase((_, _) => throw new ServerTaskOwnedByLiveProcessException(42, "pod-a:1:x")));

        var liveRun = live.ProcessAsync(42, CancellationToken.None);
        await liveInPhase.Task;

        await duplicate.ProcessAsync(42, CancellationToken.None);
        releaseLive.SetResult();
        await liveRun;

        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The live run registered BEFORE the duplicate on the other side of the race: the duplicate got there first, the live run claims second.
    // Promotion on claim must still make the live run the cancel target.
    [Fact]
    public async Task RunThatClaimsAfterAFencedDuplicateRegistered_IsTheCancelTarget()
    {
        var duplicateRegistered = _registry.Register(42);
        var inPhase = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var cancelReached = false;

        var live = Runner(ClaimPhase(), WorkPhase(async (_, ct) =>
        {
            inPhase.SetResult();
            await release.Task;
            cancelReached = ct.IsCancellationRequested;
        }));

        var liveRun = live.ProcessAsync(42, CancellationToken.None);
        await inPhase.Task;

        _registry.TryCancel(42).ShouldBeTrue();
        release.SetResult();
        await liveRun;

        cancelReached.ShouldBeTrue("a cancel must reach the run that claimed the task, not the earlier-registered unclaimed one");
        duplicateRegistered.IsCancellationRequested.ShouldBeFalse();
    }

    private DeploymentPipelineRunner Runner(params IDeploymentPipelinePhase[] phases)
        => new(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());

    // Stands in for LoadTaskPhase: the claim succeeded (a lease exists); the pipeline's work runs in later phases.
    private static IDeploymentPipelinePhase ClaimPhase() => PlainPhase((ctx, _) => ctx.Lease = new NoopLease());

    private static IDeploymentPipelinePhase WorkPhase(Func<DeploymentTaskContext, CancellationToken, Task> body)
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(2);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).Returns<DeploymentTaskContext, CancellationToken>(body);

        return phase.Object;
    }

    private static IDeploymentPipelinePhase PlainPhase(Action<DeploymentTaskContext, CancellationToken> body)
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).Returns<DeploymentTaskContext, CancellationToken>((ctx, ct) => { body(ctx, ct); return Task.CompletedTask; });

        return phase.Object;
    }

    private sealed class NoopLease : IServerTaskLease
    {
        public bool IsLost => false;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
