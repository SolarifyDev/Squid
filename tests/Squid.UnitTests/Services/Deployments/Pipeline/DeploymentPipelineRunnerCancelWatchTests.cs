using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Cancel;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Core.Services.Jobs;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// Wiring between the runner and the cross-pod cancel watcher: the watcher is only started once the pipeline holds the task's lease, signals
/// through the SAME registry cancel a same-pod cancel uses, and is disposed on every exit path. Deleting the start call or the disposal fails these.
/// </summary>
public class DeploymentPipelineRunnerCancelWatchTests
{
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly TaskCancellationRegistry _registry = new();
    private readonly RecordingWatcher _watcher = new();

    [Fact]
    public async Task ClaimedPipeline_StartsTheWatchForThatTask_WhileLaterPhasesRun_ThenDisposesIt()
    {
        var watchedDuringLaterPhase = false;

        await Runner(Claim(), Phase(2, _ => watchedDuringLaterPhase = _watcher.Started.Count == 1 && !_watcher.Watches[0].Disposed)).ProcessAsync(42, CancellationToken.None);

        _watcher.Started.ShouldBe([42]);
        watchedDuringLaterPhase.ShouldBeTrue("the watch must be live while the steps run");
        _watcher.Watches[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task UnclaimedPipeline_NeverStartsTheWatch()
    {
        await Runner(Phase(1, _ => { }), Phase(2, _ => { })).ProcessAsync(42, CancellationToken.None);

        _watcher.Started.ShouldBeEmpty("a duplicate delivery that holds no lease must never be cancelled on another pod's behalf");
    }

    [Fact]
    public async Task WatchSignal_TripsTheSameRegistryToken_SoThePipelineEndsCancelled()
    {
        await Runner(Claim(), Phase(2, ct => { _watcher.Watches[0].Fire(); ct.ThrowIfCancellationRequested(); })).ProcessAsync(42, CancellationToken.None);

        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WatchIsDisposed_WhenThePipelineFails()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => Runner(Claim(), Phase(2, _ => throw new InvalidOperationException("boom"))).ProcessAsync(42, CancellationToken.None));

        _watcher.Watches[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task WatchIsDisposed_WhenThePipelinePauses()
    {
        await Runner(Claim(), Phase(2, _ => throw new DeploymentSuspendedException(42))).ProcessAsync(42, CancellationToken.None);

        _watcher.Watches[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task WatchIsDisposed_WhenTheHostShutsDown()
    {
        using var cts = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => Runner(Claim(), Phase(2, _ => { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); })).ProcessAsync(42, cts.Token));

        _watcher.Watches[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task WatchIsDisposed_WhenTheDeploymentTimesOut()
    {
        var runner = new DeploymentPipelineRunner([Claim(), Phase(2, ct => { ct.WaitHandle.WaitOne(); ct.ThrowIfCancellationRequested(); })], _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>(), _watcher)
        {
            DeploymentTimeout = TimeSpan.FromMilliseconds(50)
        };

        await runner.ProcessAsync(42, CancellationToken.None);

        _watcher.Watches[0].Disposed.ShouldBeTrue();
    }

    private DeploymentPipelineRunner Runner(params IDeploymentPipelinePhase[] phases)
        => new(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>(), _watcher);

    // Stands in for LoadTaskPhase: the claim succeeded, so the lease is held.
    private IDeploymentPipelinePhase Claim() => Build(1, (ctx, _) => { ctx.Lease = new HeldLease(); });

    private static IDeploymentPipelinePhase Phase(int order, Action<CancellationToken> body) => Build(order, (_, ct) => body(ct));

    private static IDeploymentPipelinePhase Build(int order, Action<DeploymentTaskContext, CancellationToken> body)
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(order);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).Returns<DeploymentTaskContext, CancellationToken>((ctx, ct) => { body(ctx, ct); return Task.CompletedTask; });

        return phase.Object;
    }

    private sealed class HeldLease : IServerTaskLease
    {
        public bool IsLost => false;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingWatcher : IServerTaskCancelWatcher
    {
        public List<int> Started { get; } = new();

        public List<RecordingWatch> Watches { get; } = new();

        public IAsyncDisposable Start(int serverTaskId, Action onCancelRequested)
        {
            Started.Add(serverTaskId);

            var watch = new RecordingWatch(onCancelRequested);
            Watches.Add(watch);

            return watch;
        }
    }

    private sealed class RecordingWatch(Action onCancelRequested) : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public void Fire() => onCancelRequested();

        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return ValueTask.CompletedTask;
        }
    }
}
