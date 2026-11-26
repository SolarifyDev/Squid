using System;
using System.Linq;
using Halibut;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using Squid.Message.Enums.Deployments;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// Every way a run ends announces (lifecycle event → audit history, task log, activity node) the outcome the completion handler reports it actually wrote —
/// never the one the run set out to write. A write that yielded to a user's cancel is announced as that cancel, exactly once; a write that recorded nothing
/// (the task belongs to another run) is not announced at all. Before, every path announced its intent first and wrote second, so a deployment that ended
/// Cancelled was audited DeploymentFailed (or DeploymentSucceeded, or DeploymentTimedOut).
/// </summary>
public class DeploymentPipelineRunnerOutcomeAnnouncementTests
{
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly TaskCancellationRegistry _registry = new();
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();

    public enum EndOfRun { Success, FailedBatch, RegistryCancel, Error, Suspended, Transient, TimeoutResumable, TimeoutFailFast }

    public enum Reported { AsIntended, Cancelled, NotRecorded }

    [Theory]
    [InlineData(EndOfRun.Success, Reported.AsIntended, typeof(DeploymentSucceededEvent))]
    [InlineData(EndOfRun.Success, Reported.Cancelled, typeof(DeploymentCancelledEvent))]
    [InlineData(EndOfRun.Success, Reported.NotRecorded, null)]
    [InlineData(EndOfRun.FailedBatch, Reported.AsIntended, typeof(DeploymentFailedEvent))]
    [InlineData(EndOfRun.FailedBatch, Reported.Cancelled, typeof(DeploymentCancelledEvent))]
    [InlineData(EndOfRun.FailedBatch, Reported.NotRecorded, null)]
    [InlineData(EndOfRun.RegistryCancel, Reported.AsIntended, typeof(DeploymentCancelledEvent))]
    [InlineData(EndOfRun.RegistryCancel, Reported.NotRecorded, null)]
    [InlineData(EndOfRun.Error, Reported.AsIntended, typeof(DeploymentFailedEvent))]
    [InlineData(EndOfRun.Error, Reported.Cancelled, typeof(DeploymentCancelledEvent))]
    [InlineData(EndOfRun.Error, Reported.NotRecorded, null)]
    [InlineData(EndOfRun.Suspended, Reported.AsIntended, typeof(DeploymentPausedEvent))]
    [InlineData(EndOfRun.Suspended, Reported.Cancelled, typeof(DeploymentCancelledEvent))]
    [InlineData(EndOfRun.Suspended, Reported.NotRecorded, null)]
    [InlineData(EndOfRun.Transient, Reported.AsIntended, typeof(DeploymentPausedEvent))]
    [InlineData(EndOfRun.Transient, Reported.Cancelled, typeof(DeploymentCancelledEvent))]
    [InlineData(EndOfRun.TimeoutResumable, Reported.AsIntended, typeof(DeploymentTimedOutEvent))]
    [InlineData(EndOfRun.TimeoutResumable, Reported.Cancelled, typeof(DeploymentCancelledEvent))]
    [InlineData(EndOfRun.TimeoutResumable, Reported.NotRecorded, null)]
    [InlineData(EndOfRun.TimeoutFailFast, Reported.AsIntended, typeof(DeploymentTimedOutEvent))]
    [InlineData(EndOfRun.TimeoutFailFast, Reported.Cancelled, typeof(DeploymentCancelledEvent))]
    public async Task TheRunAnnouncesTheOutcomeThatWasWritten(EndOfRun end, Reported reported, Type expected)
    {
        ReportFromEveryCallback(end, reported);

        try { await CreateRunner(end).ProcessAsync(1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)); } catch (InvalidOperationException) { }

        var announced = TerminalAnnouncements();

        if (expected == null) announced.ShouldBeEmpty();
        else announced.ShouldBe([expected], customMessage: $"{end} reported {reported}: exactly one announcement, for the outcome that was written");
    }

    // A write that fails outright leaves no persisted outcome: nothing is announced from the catch paths (the failure is logged).
    [Theory]
    [InlineData(EndOfRun.Error)]
    [InlineData(EndOfRun.Suspended)]
    [InlineData(EndOfRun.TimeoutResumable)]
    public async Task AWriteThatFails_AnnouncesNothing(EndOfRun end)
    {
        _completion.Setup(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("db down"));
        _completion.Setup(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("db down"));
        _completion.Setup(c => c.OnTimedOutAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("db down"));

        try { await CreateRunner(end).ProcessAsync(1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)); } catch (InvalidOperationException) { }

        TerminalAnnouncements().ShouldBeEmpty();
    }

    private void ReportFromEveryCallback(EndOfRun end, Reported reported)
    {
        var outcome = reported switch
        {
            Reported.Cancelled => DeploymentOutcome.Cancelled,
            Reported.NotRecorded => DeploymentOutcome.NotRecorded,
            _ => IntendedOutcomeOf(end)
        };

        _completion.Setup(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        _completion.Setup(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        _completion.Setup(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        _completion.Setup(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        _completion.Setup(c => c.OnTimedOutAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        _completion.Setup(c => c.OnTransientPauseAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
    }

    private static DeploymentOutcome IntendedOutcomeOf(EndOfRun end) => end switch
    {
        EndOfRun.Success => DeploymentOutcome.Succeeded,
        EndOfRun.FailedBatch or EndOfRun.Error or EndOfRun.TimeoutFailFast => DeploymentOutcome.Failed,
        EndOfRun.RegistryCancel => DeploymentOutcome.Cancelled,
        _ => DeploymentOutcome.Paused
    };

    private List<Type> TerminalAnnouncements()
        => _lifecycle.Invocations
            .Where(i => i.Method.Name == nameof(IDeploymentLifecycle.EmitAsync))
            .Select(i => i.Arguments[0].GetType())
            .Where(t => t == typeof(DeploymentSucceededEvent) || t == typeof(DeploymentFailedEvent) || t == typeof(DeploymentCancelledEvent) || t == typeof(DeploymentPausedEvent) || t == typeof(DeploymentTimedOutEvent))
            .ToList();

    private DeploymentPipelineRunner CreateRunner(EndOfRun end)
        => new([PhaseEnding(end)], _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>())
        {
            DeploymentTimeout = end is EndOfRun.TimeoutResumable or EndOfRun.TimeoutFailFast ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromMinutes(5),
            TimeoutResumable = end != EndOfRun.TimeoutFailFast
        };

    private IDeploymentPipelinePhase PhaseEnding(EndOfRun end)
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentTaskContext, CancellationToken>(async (ctx, ct) =>
            {
                switch (end)
                {
                    case EndOfRun.FailedBatch: ctx.FailureEncountered = true; break;
                    case EndOfRun.RegistryCancel: _registry.TryCancel(1); break;
                    case EndOfRun.Error: throw new InvalidOperationException("boom");
                    case EndOfRun.Suspended: throw new DeploymentSuspendedException(1);
                    case EndOfRun.Transient: throw new HalibutClientException("connection reset by peer");
                    case EndOfRun.TimeoutResumable or EndOfRun.TimeoutFailFast: await Task.Delay(Timeout.Infinite, ct); break;
                }
            });

        return phase.Object;
    }
}
