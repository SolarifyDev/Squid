using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Cancel;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Core.Services.Jobs;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// Two runs of one task in one process (a lease takeover while the first is still unwinding) share the task's registry entry. The run that owns the
/// task now is the cancel target, and a run that is older, or has lost its lease, must never displace it; a cross-pod cancel signals the run that
/// observed it, not whichever source the registry holds for the task id. Each test fails if the matching guard in the runner is deleted.
/// </summary>
public class DeploymentPipelineRunnerPromotionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly TaskCancellationRegistry _registry = new();
    private readonly RecordingWatcher _watcher = new();

    // Run A claims first, run B (the takeover) claims while A is still running; A then finishes another phase. A's promote must not run again.
    [Fact]
    public async Task AnOlderRunsLaterPhases_DoNotDisplaceTheNewerRunsCancelTarget()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdA = new HoldPhase(3);
        var holdB = new HoldPhase(2);
        using var externalA = new CancellationTokenSource();

        var runA = Runner(new ClaimPhase(new FakeLease()), new DelegatePhase(2, async (_, _) => { gateReached.SetResult(); await gate.Task; }), holdA).ProcessAsync(42, externalA.Token);
        await gateReached.Task.WaitAsync(Bound);

        var runB = Runner(new ClaimPhase(new FakeLease()), holdB).ProcessAsync(42, CancellationToken.None);
        await holdB.Started.WaitAsync(Bound);

        gate.SetResult();
        await holdA.Started.WaitAsync(Bound);

        _registry.TryCancel(42).ShouldBeTrue();
        await runB.WaitAsync(Bound);

        holdB.Cancelled.ShouldBeTrue("the user's cancel must reach the run that owns the task now");
        holdA.Cancelled.ShouldBeFalse("the older run must not have become the cancel target again");

        await externalA.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => runA.WaitAsync(Bound));
    }

    [Fact]
    public async Task ARunThatLostItsLease_NeverBecomesTheCancelTarget()
    {
        var holdB = new HoldPhase(2);

        var runB = Runner(new ClaimPhase(new FakeLease()), holdB).ProcessAsync(42, CancellationToken.None);
        await holdB.Started.WaitAsync(Bound);

        var lost = new FakeLease();
        lost.Lose();
        await Runner(new ClaimPhase(lost)).ProcessAsync(42, CancellationToken.None).WaitAsync(Bound);

        _registry.TryCancel(42).ShouldBeTrue("the lost run neither displaced the owner nor unregistered it");
        await runB.WaitAsync(Bound);

        holdB.Cancelled.ShouldBeTrue();
    }

    // The watcher is one-shot: if its signal went to whatever the registry holds for the task id, a run could cancel a different run and then stop watching.
    [Fact]
    public async Task TheCancelWatchSignal_CancelsTheRunThatObservedIt_NotWhateverTheRegistryHolds()
    {
        using var foreign = new CancellationTokenSource();

        await Runner(new ClaimPhase(new FakeLease()), new DelegatePhase(2, (_, ct) =>
        {
            _registry.Promote(42, foreign);
            _watcher.Watches[0].Fire();
            ct.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        })).ProcessAsync(42, CancellationToken.None).WaitAsync(Bound);

        foreign.IsCancellationRequested.ShouldBeFalse();
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private DeploymentPipelineRunner Runner(params IDeploymentPipelinePhase[] phases) => new(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>(), _watcher);

    private sealed class ClaimPhase(FakeLease lease) : IDeploymentPipelinePhase
    {
        public int Order => 1;

        public Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
        {
            ctx.Lease = lease;

            return Task.CompletedTask;
        }
    }

    private sealed class DelegatePhase(int order, Func<DeploymentTaskContext, CancellationToken, Task> body) : IDeploymentPipelinePhase
    {
        public int Order => order;

        public Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct) => body(ctx, ct);
    }

    private sealed class HoldPhase(int order) : IDeploymentPipelinePhase
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Order => order;

        public Task Started => _started.Task;

        public bool Cancelled { get; private set; }

        public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
        {
            _started.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }
    }

    private sealed class FakeLease : IServerTaskLease
    {
        private readonly CancellationTokenSource _lost = new();

        public bool IsLost { get; private set; }

        public CancellationToken LostToken => _lost.Token;

        public void Release()
        {
        }

        public void Fence() => Lose();

        public void Lose()
        {
            IsLost = true;

            _lost.Cancel();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingWatcher : IServerTaskCancelWatcher
    {
        public List<RecordingWatch> Watches { get; } = new();

        public IAsyncDisposable Start(int serverTaskId, Action onCancelRequested)
        {
            var watch = new RecordingWatch(onCancelRequested);

            Watches.Add(watch);

            return watch;
        }
    }

    private sealed class RecordingWatch(Action onCancelRequested) : IAsyncDisposable
    {
        public void Fire() => onCancelRequested();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
