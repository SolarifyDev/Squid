using Autofac;
using Squid.Core.Services.Deployments.ServerTask.Lease;

namespace Squid.UnitTests.Services.Deployments.ServerTask.Lease;

public class ServerTaskLeaseKeeperTests : IDisposable
{
    private static readonly string Owner = ServerTaskLeaseOwner.Current;

    private readonly Mock<IServerTaskLeaseDataProvider> _provider = new();
    private readonly IContainer _container;
    private readonly ServerTaskLeaseKeeper _keeper;

    public ServerTaskLeaseKeeperTests()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(_provider.Object).As<IServerTaskLeaseDataProvider>();
        _container = builder.Build();

        // A short real interval: the renewal runs on its own timer, which is exactly what is under test.
        _keeper = new ServerTaskLeaseKeeper(_container.Resolve<ILifetimeScope>(), TimeProvider.System, new ServerTaskLeaseOptions { RenewInterval = TimeSpan.FromMilliseconds(30) });
    }

    public void Dispose() => _container.Dispose();

    [Fact]
    public async Task Start_AcquiresImmediately_ThenRenewsOnItsOwnTimer()
    {
        _provider.Setup(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _provider.Setup(p => p.RenewAsync(5, Owner, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await using var lease = _keeper.Start(5);

        await WaitUntilAsync(() => Renewals() >= 3);

        _provider.Verify(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        lease.IsLost.ShouldBeFalse();
    }

    [Fact]
    public async Task Dispose_StopsRenewal()
    {
        _provider.Setup(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _provider.Setup(p => p.RenewAsync(5, Owner, It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var lease = _keeper.Start(5);
        await WaitUntilAsync(() => Renewals() >= 1);

        await lease.DisposeAsync();
        var afterDispose = Renewals();
        await Task.Delay(200);

        Renewals().ShouldBe(afterDispose, "no renewal may happen once the lease is released");
    }

    // The task may still be Pending/Paused when the loop starts (the claim comes a moment later): zero rows before the first success is
    // "not claimed yet", never "lost", and the keeper keeps trying to acquire until the claim lands.
    [Fact]
    public async Task ZeroRowsBeforeTheClaim_IsNotLost_AndAcquireRetriesUntilHeld()
    {
        _provider.SetupSequence(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(0).ReturnsAsync(0).ReturnsAsync(1);
        _provider.Setup(p => p.RenewAsync(5, Owner, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await using var lease = _keeper.Start(5);

        await WaitUntilAsync(() => Renewals() >= 1);

        lease.IsLost.ShouldBeFalse();
        _provider.Verify(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // Once held, a renewal that updates nothing means the reaper (or a takeover) moved the row: renewal stops for good so it can never undo that.
    [Fact]
    public async Task RenewalUpdatingNothing_AfterBeingHeld_IsLost_AndRenewalStops()
    {
        _provider.Setup(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _provider.SetupSequence(p => p.RenewAsync(5, Owner, It.IsAny<CancellationToken>())).ReturnsAsync(1).ReturnsAsync(0).ReturnsAsync(1);

        await using var lease = _keeper.Start(5);

        await WaitUntilAsync(() => lease.IsLost);
        await Task.Delay(200);

        lease.IsLost.ShouldBeTrue();
        Renewals().ShouldBe(2, "after losing the lease the keeper must not renew again");
    }

    // The keeper starts after a claim that already fenced out a live foreign owner, so a denial should not happen. If it does (someone took the
    // lease in between) it is NOT "lost" (this process never held it): the keeper keeps retrying each interval rather than giving up or flagging the pipeline.
    [Fact]
    public async Task AcquireDeniedByALiveForeignOwner_IsNeverLost_AndKeepsRetrying()
    {
        _provider.Setup(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        await using var lease = _keeper.Start(5);

        await WaitUntilAsync(() => System.Linq.Enumerable.Count(_provider.Invocations, i => i.Method.Name == nameof(IServerTaskLeaseDataProvider.AcquireAsync)) >= 4);

        lease.IsLost.ShouldBeFalse();
        Renewals().ShouldBe(0, "a lease that was never held is acquired, not renewed");
    }

    [Fact]
    public async Task RenewalThrowing_DoesNotKillTheLoop_AndIsNotLost()
    {
        _provider.Setup(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var calls = 0;
        _provider.Setup(p => p.RenewAsync(5, Owner, It.IsAny<CancellationToken>())).Returns(() => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("db blip") : Task.FromResult(1));

        await using var lease = _keeper.Start(5);

        await WaitUntilAsync(() => Renewals() >= 3);

        lease.IsLost.ShouldBeFalse();
    }

    private int Renewals() => System.Linq.Enumerable.Count(_provider.Invocations, i => i.Method.Name == nameof(IServerTaskLeaseDataProvider.RenewAsync));

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
