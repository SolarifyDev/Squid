using System;
using Halibut;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentPipelineRunnerCancellationTests
{
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly TaskCancellationRegistry _registry = new();
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();

    [Fact]
    public async Task Success_CallsOnSuccessAndUnregisters()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        var runner = CreateRunner(phase.Object);

        await runner.ProcessAsync(1, CancellationToken.None);

        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _registry.TryCancel(1).ShouldBeFalse();
    }

    [Fact]
    public async Task Suspended_CallsOnPausedAndDoesNotRethrow()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DeploymentSuspendedException(1));
        var runner = CreateRunner(phase.Object);

        await runner.ProcessAsync(1, CancellationToken.None);

        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentPausedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Suspended_PropagatesTheOperatorReasonOntoThePausedEvent()
    {
        // The reason exists to reach the operator's activity log, and the runner is the only link
        // between the throw site and that log. Verifying merely that SOME DeploymentPausedEvent
        // was emitted leaves this link free to drop the reason silently.
        const string reason = "because the master key rotated";

        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DeploymentSuspendedException(1, reason));
        var runner = CreateRunner(phase.Object);

        await runner.ProcessAsync(1, CancellationToken.None);

        _lifecycle.Verify(l => l.EmitAsync(It.Is<DeploymentPausedEvent>(e => e.Context.PauseReason == reason), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancellationViaRegistry_CallsOnCancelledAndDoesNotRethrow()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentTaskContext, CancellationToken>(async (_, ct) =>
            {
                _registry.TryCancel(1);
                ct.ThrowIfCancellationRequested();
            });
        var runner = CreateRunner(phase.Object);

        await runner.ProcessAsync(1, CancellationToken.None);

        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentCancelledEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancellationViaExternalToken_CallsOnCancelledAndDoesNotRethrow()
    {
        var externalCts = new CancellationTokenSource();
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentTaskContext, CancellationToken>(async (_, ct) =>
            {
                externalCts.Cancel();
                ct.ThrowIfCancellationRequested();
            });
        var runner = CreateRunner(phase.Object);

        await runner.ProcessAsync(1, externalCts.Token);

        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentCancelledEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Failure_CallsOnFailureAndRethrows()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var runner = CreateRunner(phase.Object);

        await Should.ThrowAsync<InvalidOperationException>(() => runner.ProcessAsync(1, CancellationToken.None));

        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentFailedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AlwaysUnregisters_EvenOnFailure()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var runner = CreateRunner(phase.Object);

        try { await runner.ProcessAsync(1, CancellationToken.None); } catch { }

        _registry.TryCancel(1).ShouldBeFalse();
    }

    // ── P0-A.1 regression guard (2026-04-24 audit) ──────────────────────────────
    //
    // The cancel-vs-fail race: a step captures `ctx.FailureEncountered = true` on
    // the context and the USER clicks cancel in the same narrow window — before
    // the terminal event is emitted. Pre-fix, the runner checked
    // `if (ctx.FailureEncountered)` FIRST, so the task ended Failed even though
    // the operator explicitly asked to cancel. The DeploymentFailedEvent landed
    // in the checkpoint and confused every downstream consumer (retry policy,
    // auto-deploy triggers, ticket-state UI showing "cancel requested").
    //
    // The fix: before deciding terminal state, inspect the cancellation sources.
    // If the operator's registry cancel OR the caller's external token is already
    // signalled, emit DeploymentCancelledEvent + OnCancelledAsync regardless of
    // FailureEncountered. Cancel wins.

    [Fact]
    public async Task RaceCancelAfterFailure_RegistryCancelWins_CallsOnCancelledNotOnFailure()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentTaskContext, CancellationToken>((ctx, _) =>
            {
                // Simulate the race: a step failure gets captured on the context, AND
                // the user hits cancel in the same window. The phase returns normally
                // (did not propagate OCE — maybe it caught cancellation internally, or
                // the cancel request just missed the last CT check).
                ctx.FailureEncountered = true;
                _registry.TryCancel(1);
                return Task.CompletedTask;
            });

        var runner = CreateRunner(phase.Object);

        await runner.ProcessAsync(1, CancellationToken.None);

        _completion.Verify(
            c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()),
            Times.Once,
            failMessage:
                "registry cancel was requested before the terminal write — task must end Cancelled. " +
                "Pre-fix the FailureEncountered check won the race and tasks ended Failed instead.");

        _completion.Verify(
            c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()),
            Times.Never,
            failMessage: "cancel must fully win — no OnFailure on the race path");

        _lifecycle.Verify(
            l => l.EmitAsync(It.IsAny<DeploymentCancelledEvent>(), It.IsAny<CancellationToken>()),
            Times.Once,
            failMessage: "cancel path must emit DeploymentCancelledEvent, not DeploymentFailedEvent");

        _lifecycle.Verify(
            l => l.EmitAsync(It.IsAny<DeploymentFailedEvent>(), It.IsAny<CancellationToken>()),
            Times.Never,
            failMessage: "race must not emit the failed event — downstream consumers latch on it");
    }

    [Fact]
    public async Task RaceCancelAfterFailure_ExternalCtCancelWins_CallsOnCancelledNotOnFailure()
    {
        // Same race but the cancel signal comes from the caller's external token
        // (e.g. Hangfire job scope shutting down). Identical terminal-state decision
        // must apply — cancel wins over fail.
        using var externalCts = new CancellationTokenSource();

        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentTaskContext, CancellationToken>((ctx, _) =>
            {
                ctx.FailureEncountered = true;
                externalCts.Cancel();
                return Task.CompletedTask;
            });

        var runner = CreateRunner(phase.Object);

        await runner.ProcessAsync(1, externalCts.Token);

        _completion.Verify(
            c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _completion.Verify(
            c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FailureAlone_NoCancel_StillCallsOnFailure()
    {
        // Regression guard on the guard: make sure we didn't accidentally redirect
        // all failures to the cancel path. A plain failure with no cancel requested
        // still ends Failed — cancel must only win when there's actual cancellation.
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentTaskContext, CancellationToken>((ctx, _) =>
            {
                ctx.FailureEncountered = true;
                // No cancel called.
                return Task.CompletedTask;
            });

        var runner = CreateRunner(phase.Object);

        await runner.ProcessAsync(1, CancellationToken.None);

        _completion.Verify(
            c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _completion.Verify(
            c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── SafeCompleteAsync containment ────────────────────────────────────

    /// <summary>
    /// Every terminal-handling catch path routes its completion through SafeCompleteAsync so a
    /// failing completion handler cannot escape ProcessAsync. That matters because the handlers
    /// WRITE STATE and can legitimately fail: TransitionStateAsync issues a conditional UPDATE
    /// and throws when it matches no row, which a cancel landing between the handler's state read
    /// and its write still produces. An escaping exception would surface as a hard job failure
    /// contradicting the terminal outcome the pipeline had already decided on.
    ///
    /// <para>Pinned as one Theory over all five paths rather than per-path facts: the contract
    /// belongs to the helper, and a per-path test would leave the siblings free to regress.</para>
    /// </summary>
    public enum TerminalPath { Suspended, Timeout, Cancelled, Transient, Failed }

    [Theory]
    [InlineData(TerminalPath.Suspended)]
    [InlineData(TerminalPath.Timeout)]
    [InlineData(TerminalPath.Cancelled)]
    [InlineData(TerminalPath.Transient)]
    public async Task CompletionHandlerThrows_DoesNotEscapeTheRunner(TerminalPath path)
    {
        ArrangeCompletionToThrow(path);

        var runner = CreateFastTimeoutRunner(PhaseFor(path));

        await Should.NotThrowAsync(() => runner.ProcessAsync(1, CancellationToken.None),
            customMessage: $"A failing completion handler on the {path} path must be contained by SafeCompleteAsync. " +
                           "Escaping here fails the Hangfire job and contradicts the terminal outcome already chosen.");
    }

    [Theory]
    [InlineData(TerminalPath.Suspended)]
    [InlineData(TerminalPath.Timeout)]
    [InlineData(TerminalPath.Cancelled)]
    [InlineData(TerminalPath.Transient)]
    [InlineData(TerminalPath.Failed)]
    public async Task LifecycleEmitThrows_StillRunsTheCompletionHandler(TerminalPath path)
    {
        // The state write must not be skipped because an activity-log emit failed — the direct
        // call this replaced had exactly that flaw: an emit failure short-circuited the handler.
        _lifecycle.Setup(l => l.EmitAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("activity log unavailable"));

        var runner = CreateFastTimeoutRunner(PhaseFor(path));

        // The generic failure path deliberately rethrows the ORIGINAL exception (see below), so
        // only assert containment of the emit failure itself.
        try
        {
            await runner.ProcessAsync(1, CancellationToken.None);
        }
        catch (InvalidOperationException ex) when (path == TerminalPath.Failed && ex.Message == StepFailureMessage)
        {
            // expected on the failure path
        }

        VerifyCompletionRan(path);
    }

    [Fact]
    public async Task Failed_CompletionHandlerThrows_TheOriginalFailureSurfaces_NotTheHandlers()
    {
        // The failure path is deliberately NOT symmetric with the other four: it rethrows so a
        // genuine step failure surfaces as a failed job. What SafeCompleteAsync must still
        // guarantee is that a failing OnFailureAsync does not REPLACE that outcome — the
        // operator has to see why the deployment failed, not why the bookkeeping did.
        ArrangeCompletionToThrow(TerminalPath.Failed);

        var runner = CreateFastTimeoutRunner(PhaseFor(TerminalPath.Failed));

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() => runner.ProcessAsync(1, CancellationToken.None));

        thrown.Message.ShouldBe(StepFailureMessage,
            customMessage: "The original step failure must surface. Seeing the completion handler's message instead " +
                           "means its exception escaped SafeCompleteAsync and masked the real cause.");
    }

    private const string StepFailureMessage = "step blew up";

    private IDeploymentPipelinePhase PhaseFor(TerminalPath path)
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);

        var registry = _registry;

        Exception thrown = path switch
        {
            TerminalPath.Suspended => new DeploymentSuspendedException(1, "reason"),
            TerminalPath.Transient => new HalibutClientException("transient agent blip"),
            TerminalPath.Failed => new InvalidOperationException(StepFailureMessage),
            _ => null
        };

        if (thrown != null)
            phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).ThrowsAsync(thrown);
        else if (path == TerminalPath.Cancelled)
            phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
                .Returns<DeploymentTaskContext, CancellationToken>(async (_, token) =>
                {
                    registry.TryCancel(1);
                    token.ThrowIfCancellationRequested();
                    await Task.CompletedTask;
                });
        else // Timeout
            phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
                .Returns(async (DeploymentTaskContext _, CancellationToken token) => await Task.Delay(Timeout.Infinite, token));

        return phase.Object;
    }

    private void ArrangeCompletionToThrow(TerminalPath path)
    {
        var boom = new InvalidOperationException("completion handler failed");

        switch (path)
        {
            case TerminalPath.Suspended:
                _completion.Setup(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom); break;
            case TerminalPath.Timeout:
                _completion.Setup(c => c.OnTimedOutAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom); break;
            case TerminalPath.Cancelled:
                _completion.Setup(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom); break;
            case TerminalPath.Transient:
                _completion.Setup(c => c.OnTransientPauseAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom); break;
            case TerminalPath.Failed:
                _completion.Setup(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom); break;
        }
    }

    private void VerifyCompletionRan(TerminalPath path)
    {
        switch (path)
        {
            case TerminalPath.Suspended:
                _completion.Verify(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once); break;
            case TerminalPath.Timeout:
                _completion.Verify(c => c.OnTimedOutAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Once); break;
            case TerminalPath.Cancelled:
                _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once); break;
            case TerminalPath.Transient:
                _completion.Verify(c => c.OnTransientPauseAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Once); break;
            case TerminalPath.Failed:
                _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Once); break;
        }
    }

    /// <summary>
    /// Runner with a 50 ms deployment budget so the Timeout path resolves promptly instead of
    /// waiting on the real 60-minute default.
    /// </summary>
    private DeploymentPipelineRunner CreateFastTimeoutRunner(params IDeploymentPipelinePhase[] phases)
        => new(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>())
        {
            DeploymentTimeout = TimeSpan.FromMilliseconds(50)
        };

    private DeploymentPipelineRunner CreateRunner(params IDeploymentPipelinePhase[] phases)
    {
        return new DeploymentPipelineRunner(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());
    }
}
