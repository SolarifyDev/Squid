using System;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;

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

    private DeploymentPipelineRunner CreateRunner(params IDeploymentPipelinePhase[] phases)
    {
        return new DeploymentPipelineRunner(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object);
    }
}
