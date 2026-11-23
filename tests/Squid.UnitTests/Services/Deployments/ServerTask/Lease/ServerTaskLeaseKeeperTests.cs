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

    // Ownership belongs to a run: the keeper renews on behalf of the run it was started for, never the process, so a second run in this process is a stranger.
    [Fact]
    public async Task Start_ForARun_AcquiresAndRenewsAsThatRun_NotAsTheProcess()
    {
        const string run = "host:1:proc/run-a";
        _provider.Setup(p => p.AcquireAsync(5, run, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _provider.Setup(p => p.RenewAsync(5, run, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await using var lease = _keeper.Start(5, run);

        await WaitUntilAsync(() => Renewals() >= 2);

        _provider.Verify(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        _provider.Verify(p => p.RenewAsync(5, Owner, It.IsAny<CancellationToken>()), Times.Never);
        lease.IsLost.ShouldBeFalse();
    }

    // A run that pauses its own task clears the lease itself, so the next renewal finds nothing to update: that is the run's own doing, not a loss.
    [Fact]
    public async Task Release_StopsRenewal_AndTheZeroRowRenewalThatFollowsIsNotALoss()
    {
        _provider.Setup(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _provider.Setup(p => p.RenewAsync(5, Owner, It.IsAny<CancellationToken>())).ReturnsAsync(1);
        await using var lease = _keeper.Start(5);
        await WaitUntilAsync(() => Renewals() >= 1);

        lease.Release();
        _provider.Setup(p => p.RenewAsync(5, Owner, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        await Task.Delay(200);

        lease.IsLost.ShouldBeFalse();
        lease.LostToken.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task ARenewalThatReachesTheDatabaseAfterRelease_IsNotALoss()
    {
        var beat = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.Setup(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _provider.SetupSequence(p => p.RenewAsync(5, Owner, It.IsAny<CancellationToken>())).ReturnsAsync(1).Returns(beat.Task);
        await using var lease = _keeper.Start(5);
        await WaitUntilAsync(() => Renewals() >= 2);

        lease.Release();
        beat.SetResult(0);
        await Task.Delay(100);

        lease.IsLost.ShouldBeFalse("the renewal was already in flight when the run released its own lease");
    }

    [Fact]
    public async Task LosingTheLease_CancelsTheLostToken()
    {
        _provider.Setup(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _provider.SetupSequence(p => p.RenewAsync(5, Owner, It.IsAny<CancellationToken>())).ReturnsAsync(1).ReturnsAsync(0);

        await using var lease = _keeper.Start(5);

        await WaitUntilAsync(() => lease.LostToken.IsCancellationRequested);

        lease.IsLost.ShouldBeTrue();
    }

    // The reaper recovers a task after Stale of silence. A run that cannot reach the database for nearly that long must stop on its own: it has no way
    // to learn the reaper already paused its task, and resuming it would otherwise run two pipelines.
    [Fact]
    public async Task RenewalsFailingForTheSelfFenceWindow_AreALoss_EvenThoughTheDatabaseNeverSaidSo()
    {
        var keeper = ShortStaleKeeper();
        _provider.Setup(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _provider.Setup(p => p.RenewAsync(5, Owner, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));

        await using var lease = keeper.Start(5);

        await WaitUntilAsync(() => lease.LostToken.IsCancellationRequested);

        lease.IsLost.ShouldBeTrue();
    }

    [Fact]
    public async Task RenewalFailingBrieflyInsideTheWindow_IsNotALoss()
    {
        var keeper = ShortStaleKeeper();
        _provider.Setup(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var calls = 0;
        _provider.Setup(p => p.RenewAsync(5, Owner, It.IsAny<CancellationToken>())).Returns(() => Interlocked.Increment(ref calls) is 2 or 3 ? throw new InvalidOperationException("blip") : Task.FromResult(1));

        await using var lease = keeper.Start(5);

        await WaitUntilAsync(() => Renewals() >= 12);

        lease.IsLost.ShouldBeFalse();
    }

    // A beat that never returns (pool exhaustion, a stalled connection) must not stop the loop noticing that nothing was proven for too long.
    [Fact]
    public async Task ARenewalThatHangs_DoesNotPreventTheSelfFence()
    {
        var keeper = ShortStaleKeeper();
        _provider.Setup(p => p.AcquireAsync(5, Owner, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _provider.Setup(p => p.RenewAsync(5, Owner, It.IsAny<CancellationToken>())).Returns(new TaskCompletionSource<int>().Task);

        await using var lease = keeper.Start(5);

        await WaitUntilAsync(() => lease.LostToken.IsCancellationRequested);

        lease.IsLost.ShouldBeTrue();
    }

    // Stale 600ms with a 30ms interval: the self-fence window is 540ms, long enough that scheduler jitter never trips it in the "brief" test.
    private ServerTaskLeaseKeeper ShortStaleKeeper()
        => new(_container.Resolve<ILifetimeScope>(), TimeProvider.System, new ServerTaskLeaseOptions { RenewInterval = TimeSpan.FromMilliseconds(30), Stale = TimeSpan.FromMilliseconds(600) });

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
