using Autofac;
using Squid.Core.Services.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportConfirmationHeartbeatTests
{
    private static readonly OctopusImportConfirmationResilienceOptions Options = new() { HeartbeatInterval = TimeSpan.FromSeconds(30) };

    private readonly Mock<IOctopusImportSessionDataProvider> _dataProvider = new();
    private readonly SemaphoreSlim _touched = new(0);
    private readonly ManualTimers _time = new();
    private int _scopes;

    public OctopusImportConfirmationHeartbeatTests()
    {
        _dataProvider.Setup(p => p.TouchConfirmationHeartbeatAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(1).Callback(() => _touched.Release());
    }

    // Each beat resolves the data provider in a scope of its own: its own DbContext and connection, never the import transaction's.
    [Fact]
    public async Task BeatsEveryInterval_OnANewScope_ForTheAttempt()
    {
        var (sessionId, attemptId) = (Guid.NewGuid(), Guid.NewGuid());
        await using var container = Container();

        await using (new OctopusImportConfirmationHeartbeat(container, _time, Options).Start(sessionId, attemptId))
        {
            for (var i = 0; i < 3; i++) await TickAndWaitAsync();
        }

        _dataProvider.Verify(p => p.TouchConfirmationHeartbeatAsync(sessionId, attemptId, It.IsAny<CancellationToken>()), Times.Exactly(3));
        _scopes.ShouldBe(3);
        _time.Periods.ShouldContain(Options.HeartbeatInterval);
    }

    [Fact]
    public async Task StopsOnDispose()
    {
        await using var container = Container();
        var beat = new OctopusImportConfirmationHeartbeat(container, _time, Options).Start(Guid.NewGuid(), Guid.NewGuid());

        await TickAndWaitAsync();
        await beat.DisposeAsync();
        _time.Tick();

        (await _touched.WaitAsync(TimeSpan.FromMilliseconds(200))).ShouldBeFalse("no beat after the handle is disposed");
        _dataProvider.Verify(p => p.TouchConfirmationHeartbeatAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // A failed beat (the database blipped) must not stop the import, nor the beats after it.
    [Fact]
    public async Task BeatFailure_LoggedAndLoopContinues()
    {
        _dataProvider.SetupSequence(p => p.TouchConfirmationHeartbeatAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(() => { _touched.Release(); return Task.FromException<int>(new InvalidOperationException("db down")); })
            .Returns(() => { _touched.Release(); return Task.FromResult(1); });
        await using var container = Container();

        await using (new OctopusImportConfirmationHeartbeat(container, _time, Options).Start(Guid.NewGuid(), Guid.NewGuid()))
        {
            await TickAndWaitAsync();
            await TickAndWaitAsync();
        }

        _dataProvider.Verify(p => p.TouchConfirmationHeartbeatAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private async Task TickAndWaitAsync()
    {
        await _time.WaitForTimerAsync();
        _time.Tick();

        (await _touched.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue("the heartbeat did not beat within 5s of its timer firing");
    }

    private IContainer Container()
    {
        var builder = new ContainerBuilder();
        builder.Register(_ => { Interlocked.Increment(ref _scopes); return _dataProvider.Object; }).As<IOctopusImportSessionDataProvider>().InstancePerLifetimeScope();

        return builder.Build();
    }
}
