using Autofac;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Cancel;

namespace Squid.UnitTests.Services.Deployments.ServerTask.Cancel;

public class ServerTaskCancelWatcherTests : IDisposable
{
    private readonly Mock<IServerTaskDataProvider> _provider = new();
    private readonly IContainer _container;
    private readonly ServerTaskCancelWatcher _watcher;
    private int _signals;

    public ServerTaskCancelWatcherTests()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(_provider.Object).As<IServerTaskDataProvider>();
        _container = builder.Build();

        _watcher = new ServerTaskCancelWatcher(_container.Resolve<ILifetimeScope>(), new ServerTaskCancelWatchOptions { PollInterval = TimeSpan.FromMilliseconds(20) });
    }

    public void Dispose() => _container.Dispose();

    [Fact]
    public async Task CancelRequested_SignalsOnce_ThenStopsPolling()
    {
        _provider.Setup(p => p.IsCancelRequestedAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await using var watch = _watcher.Start(7, () => Interlocked.Increment(ref _signals));

        await WaitUntilAsync(() => _signals >= 1);
        var probes = Probes();
        await Task.Delay(150);

        _signals.ShouldBe(1);
        Probes().ShouldBe(probes, "a one-shot signal must stop the loop");
    }

    [Fact]
    public async Task NotCancelRequested_NeverSignals_ButKeepsPolling()
    {
        _provider.Setup(p => p.IsCancelRequestedAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await using var watch = _watcher.Start(7, () => Interlocked.Increment(ref _signals));

        await WaitUntilAsync(() => Probes() >= 4);

        _signals.ShouldBe(0);
    }

    [Fact]
    public async Task CancelAppearsLater_IsObservedOnALaterTick()
    {
        _provider.SetupSequence(p => p.IsCancelRequestedAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(false).ReturnsAsync(false).ReturnsAsync(true);

        await using var watch = _watcher.Start(7, () => Interlocked.Increment(ref _signals));

        await WaitUntilAsync(() => _signals == 1);

        Probes().ShouldBe(3);
    }

    [Fact]
    public async Task ProbeThrows_IsNotACancel_AndPollingContinues()
    {
        _provider.SetupSequence(p => p.IsCancelRequestedAsync(7, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db blip")).ThrowsAsync(new InvalidOperationException("db blip")).ReturnsAsync(true);

        await using var watch = _watcher.Start(7, () => Interlocked.Increment(ref _signals));

        await WaitUntilAsync(() => _signals == 1);

        Probes().ShouldBe(3);
    }

    [Fact]
    public async Task SignalThrows_IsRetriedNextTick()
    {
        _provider.Setup(p => p.IsCancelRequestedAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var attempts = 0;

        await using var watch = _watcher.Start(7, () =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new ObjectDisposedException("cts");

            Interlocked.Increment(ref _signals);
        });

        await WaitUntilAsync(() => _signals == 1);

        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task Dispose_StopsPolling_AndNeverSignalsAfterwards()
    {
        _provider.Setup(p => p.IsCancelRequestedAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var watch = _watcher.Start(7, () => Interlocked.Increment(ref _signals));
        await WaitUntilAsync(() => Probes() >= 1);

        await watch.DisposeAsync();
        var afterDispose = Probes();
        _provider.Setup(p => p.IsCancelRequestedAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await Task.Delay(150);

        Probes().ShouldBe(afterDispose);
        _signals.ShouldBe(0);
    }

    private int Probes() => System.Linq.Enumerable.Count(_provider.Invocations, i => i.Method.Name == nameof(IServerTaskDataProvider.IsCancelRequestedAsync));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached within 10s");

            await Task.Delay(10);
        }
    }
}
