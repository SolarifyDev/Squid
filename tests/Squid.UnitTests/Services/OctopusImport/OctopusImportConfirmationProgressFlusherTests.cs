using System.Linq;
using Autofac;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>The progress writer: throttled to one write per interval, only when changed, on its own scope, never blocking the import.</summary>
public class OctopusImportConfirmationProgressFlusherTests
{
    private static readonly OctopusImportConfirmationResilienceOptions Options = new() { ProgressInterval = TimeSpan.FromMilliseconds(500) };

    private readonly Mock<IOctopusImportSessionDataProvider> _dataProvider = new();
    private readonly SemaphoreSlim _written = new(0);
    private readonly List<string> _writes = [];
    private readonly ManualTimers _time = new();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _attemptId = Guid.NewGuid();
    private readonly OctopusResourceNode _release = OctopusImportConfirmationProgressPlanTests.Node(OctopusResourceKind.Release);
    private int _scopes;

    public OctopusImportConfirmationProgressFlusherTests()
    {
        _dataProvider.Setup(p => p.WriteConfirmationProgressAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(1).Callback<Guid, Guid, string, CancellationToken>((_, _, json, _) => Record(json));
    }

    [Fact]
    public async Task WritesOncePerInterval_OnANewScope_OnlyWhenChanged()
    {
        var tracker = Tracker();
        await using var container = Container();

        await using (Start(container, tracker))
        {
            tracker.EnterStage(OctopusImportConfirmationStage.Importing);
            tracker.BeginItem(_release);
            await TickAndWaitAsync();

            await TickAsync();
            (await _written.WaitAsync(TimeSpan.FromMilliseconds(200))).ShouldBeFalse("nothing changed since the last write");

            tracker.CompleteItem(_release, OctopusImportResourceOutcomeState.Created);
            await TickAndWaitAsync();
        }

        _writes.Count.ShouldBe(2, "two changed ticks, and the final flush found nothing new");
        _scopes.ShouldBe(2);
        _time.Periods.ShouldContain(Options.ProgressInterval);
        _dataProvider.Verify(p => p.WriteConfirmationProgressAsync(_sessionId, _attemptId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _writes.Last().ShouldContain("\"itemsDone\":1");
    }

    [Fact]
    public async Task Dispose_WritesTheFinalSnapshot()
    {
        var tracker = Tracker();
        await using var container = Container();
        var run = Start(container, tracker);

        tracker.Finish(OctopusImportConfirmationStage.Succeeded);
        await run.DisposeAsync();

        _writes.ShouldHaveSingleItem().ShouldContain("\"stage\":\"Succeeded\"");
    }

    [Fact]
    public async Task FlushAsync_WritesNow_WithoutATick()
    {
        var tracker = Tracker();
        await using var container = Container();
        await using var run = Start(container, tracker);

        tracker.Finish(OctopusImportConfirmationStage.RolledBack);
        await run.FlushAsync();

        _writes.ShouldHaveSingleItem().ShouldContain("\"stage\":\"RolledBack\"");
    }

    // A failed write (the database blipped) must not stop the import, and the next tick writes the progress again.
    [Fact]
    public async Task WriteFailure_LoggedNotThrown_AndRetriedNextTick()
    {
        _dataProvider.SetupSequence(p => p.WriteConfirmationProgressAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => { _written.Release(); return Task.FromException<int>(new InvalidOperationException("db down")); })
            .Returns(() => { _written.Release(); return Task.FromResult(1); });
        var tracker = Tracker();
        await using var container = Container();

        await using (Start(container, tracker))
        {
            tracker.EnterStage(OctopusImportConfirmationStage.Importing);
            await TickAndWaitAsync();
            await TickAndWaitAsync();
        }

        _dataProvider.Verify(p => p.WriteConfirmationProgressAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2), "the failed snapshot is written again, then nothing is new for the final flush");
    }

    // A write stuck behind a row lock delays only the writer: the import keeps reporting progress without waiting for it.
    [Fact]
    public async Task BlockedWrite_NeverBlocksTheTracker()
    {
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dataProvider.Setup(p => p.WriteConfirmationProgressAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => { _written.Release(); return gate.Task; });
        var tracker = Tracker();
        await using var container = Container();
        var run = Start(container, tracker);

        tracker.EnterStage(OctopusImportConfirmationStage.Importing);
        await TickAndWaitAsync();

        var completing = Task.Run(() => tracker.CompleteItem(_release, OctopusImportResourceOutcomeState.Created));
        (await Task.WhenAny(completing, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBe(completing, "the tracker waited for the blocked write");

        gate.SetResult(1);
        await run.DisposeAsync();
        tracker.Snapshot().Progress.ItemsDone.ShouldBe(1);
    }

    private OctopusImportConfirmationProgressTracker Tracker() => new(_attemptId, [_release], new Dictionary<string, string>(), _time);

    private IOctopusImportConfirmationProgressRun Start(IContainer container, OctopusImportConfirmationProgressTracker tracker) => new OctopusImportConfirmationProgress(container, _time, Options).Start(_sessionId, tracker);

    private void Record(string json)
    {
        lock (_writes) _writes.Add(json);

        _written.Release();
    }

    private async Task TickAsync()
    {
        await _time.WaitForTimerAsync();
        _time.Tick();
    }

    private async Task TickAndWaitAsync()
    {
        await TickAsync();

        (await _written.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue("the progress was not written within 5s of its timer firing");
    }

    private IContainer Container()
    {
        var builder = new ContainerBuilder();
        builder.Register(_ => { Interlocked.Increment(ref _scopes); return _dataProvider.Object; }).As<IOctopusImportSessionDataProvider>().InstancePerLifetimeScope();

        return builder.Build();
    }
}
