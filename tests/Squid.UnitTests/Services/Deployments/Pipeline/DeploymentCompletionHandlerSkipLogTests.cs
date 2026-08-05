using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using Serilog.Events;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Models.Deployments.ServerTask;
using Squid.UnitTests.Support;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// Pins the log LEVEL of a skipped pause, which is load-bearing rather than cosmetic.
///
/// <para>Warning on the <c>Cancelling</c> branch, Information everywhere else. The two halves of
/// that split are load-bearing for different reasons, and the reason differs per outcome.</para>
///
/// <para>Timeout and transient pauses previously transitioned blind, so a <c>Cancelling</c> row
/// raised an illegal-transition exception that the runner's <c>SafeCompleteAsync</c> logged at
/// Error. The guard removes the exception, so Warning is what stops it silently deleting the only
/// signal that a task became permanently stuck holding its environment's concurrency slot. The
/// suspend outcome already had this guard and already logged at Information, so there Warning is a
/// deliberate uplift rather than a preservation — the same wedged state should not be reported at
/// two different levels depending on which outcome walked into it. Information everywhere else is
/// what keeps the routine <c>Paused</c> case from crying wolf.</para>
///
/// <para>Separate class from <c>DeploymentCompletionHandlerTests</c> because asserting levels
/// means swapping the global <c>Log.Logger</c>, which requires
/// <see cref="GlobalStateSerialisedCollection"/> — no reason to serialise the other forty tests
/// alongside it.</para>
/// </summary>
[Collection(GlobalStateSerialisedCollection.Name)]
public sealed class DeploymentCompletionHandlerSkipLogTests
{
    [Theory]
    [InlineData(PauseKind.Suspended)]
    [InlineData(PauseKind.TimedOut)]
    [InlineData(PauseKind.Transient)]
    public async Task SkippingBecauseTheTaskIsCancelling_WarnsThatTheSlotIsStuck(PauseKind kind)
    {
        var (sink, restore) = InstallCapturingLogger();

        try
        {
            await InvokeWithCurrentStateAsync(kind, TaskState.Cancelling);

            var skip = SingleSkipEvent(sink);

            skip.Level.ShouldBe(LogEventLevel.Warning,
                customMessage: "This is the only remaining trace that an environment lost a concurrency slot with " +
                               "no automatic recovery. For the timeout and transient outcomes the guard removed the " +
                               "Error that the swallowed transition exception used to produce; for the suspend " +
                               "outcome this is an uplift from Information. Either way, at Information it will not " +
                               "reach an operator.");

            var rendered = skip.RenderMessage();
            rendered.ShouldContain("concurrency slot",
                customMessage: "The message has to name the consequence; 'skipping the pause' alone is not actionable.");
        }
        finally
        {
            restore();
        }
    }

    [Theory]
    [InlineData(PauseKind.Suspended, TaskState.Paused)]
    [InlineData(PauseKind.Suspended, TaskState.Success)]
    [InlineData(PauseKind.TimedOut, TaskState.Paused)]
    [InlineData(PauseKind.TimedOut, TaskState.Success)]
    [InlineData(PauseKind.Transient, TaskState.Paused)]
    [InlineData(PauseKind.Transient, TaskState.Success)]
    public async Task SkippingForAnyOtherReason_StaysAtInformation(PauseKind kind, string currentState)
    {
        // Paused is the ordinary case — the suspend sites that self-transition hit it on every
        // manual intervention. Warning here would bury the Cancelling signal in routine noise.
        var (sink, restore) = InstallCapturingLogger();

        try
        {
            await InvokeWithCurrentStateAsync(kind, currentState);

            SingleSkipEvent(sink).Level.ShouldBe(LogEventLevel.Information);
        }
        finally
        {
            restore();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    public enum PauseKind { Suspended, TimedOut, Transient }

    private static LogEvent SingleSkipEvent(CapturingLogSink sink)
    {
        var events = sink.Events.Where(e => e.RenderMessage().Contains("not Executing")).ToList();

        events.Count.ShouldBe(1, "the skip should be reported exactly once per pause attempt");

        return events[0];
    }

    private static async Task InvokeWithCurrentStateAsync(PauseKind kind, string currentState)
    {
        var serverTaskService = new Mock<IServerTaskService>();
        serverTaskService.Setup(s => s.GetTaskAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskSummaryDto { Id = 1, State = currentState });

        var genericDataProvider = new Mock<IGenericDataProvider>();
        genericDataProvider.Setup(g => g.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>(async (action, ct) => await action(ct));

        var sut = new DeploymentCompletionHandler(genericDataProvider.Object, serverTaskService.Object,
            Mock.Of<IDeploymentDataProvider>(), Mock.Of<IDeploymentCompletionDataProvider>(),
            Mock.Of<IAutoDeployService>(), Mock.Of<IDeploymentCheckpointService>());

        var ctx = new DeploymentTaskContext { ServerTaskId = 1, Deployment = new Deployment { Id = 1, SpaceId = 1 } };

        await (kind switch
        {
            PauseKind.Suspended => sut.OnPausedAsync(ctx, CancellationToken.None),
            PauseKind.TimedOut => sut.OnTimedOutAsync(ctx, new Exception("timeout"), CancellationToken.None),
            PauseKind.Transient => sut.OnTransientPauseAsync(ctx, new Exception("blip"), CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });
    }

    private static (CapturingLogSink Sink, Action Restore) InstallCapturingLogger()
    {
        var original = Log.Logger;
        var sink = new CapturingLogSink();

        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        return (sink, () => Log.Logger = original);
    }

    private sealed class CapturingLogSink : Serilog.Core.ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
