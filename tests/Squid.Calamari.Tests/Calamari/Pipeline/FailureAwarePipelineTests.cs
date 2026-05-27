using Shouldly;
using Squid.Calamari.Pipeline;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Pipeline;

/// <summary>
/// Pins the contract between <see cref="ExecutionPipeline{TContext}"/> and
/// <see cref="IFailureAwareExecutionContext"/>: when any non-cleanup step
/// throws, the pipeline sets <c>ExecutionFailed = true</c> on the context
/// before propagating the exception. Cleanup-phase steps then read this to
/// decide whether to fire.
/// </summary>
public sealed class FailureAwarePipelineTests
{
    [Fact]
    public async Task NormalRun_NoExceptions_FlagStaysFalse()
    {
        var ctx = new FlagContext();
        var pipeline = new ExecutionPipeline<FlagContext>(new IExecutionStep<FlagContext>[]
        {
            new NoopStep()
        });

        await pipeline.ExecuteAsync(ctx, CancellationToken.None);

        ctx.ExecutionFailed.ShouldBeFalse(
            customMessage: "Normal pipeline run with no exceptions MUST leave ExecutionFailed=false.");
    }

    [Fact]
    public async Task NormalStepThrows_FlagSetBeforeRethrow()
    {
        var ctx = new FlagContext();
        var pipeline = new ExecutionPipeline<FlagContext>(new IExecutionStep<FlagContext>[]
        {
            new ThrowingStep("kaboom")
        });

        // Pipeline rethrows the captured exception — that's expected.
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            pipeline.ExecuteAsync(ctx, CancellationToken.None));
        ex.Message.ShouldBe("kaboom");

        ctx.ExecutionFailed.ShouldBeTrue(
            customMessage: "An exception in a normal-phase step MUST set ExecutionFailed=true so " +
                           "IAlwaysRunExecutionStep implementations can read it during cleanup.");
    }

    [Fact]
    public async Task AlwaysRunStep_ReadsFlag_RunsAfterFailingStep()
    {
        // Combined contract test — failure-aware cleanup step sees the flag.
        var ctx = new FlagContext();
        var observer = new FlagObserverAlwaysRun();
        var pipeline = new ExecutionPipeline<FlagContext>(new IExecutionStep<FlagContext>[]
        {
            new ThrowingStep("upstream failure"),
            observer
        });

        await Should.ThrowAsync<InvalidOperationException>(() =>
            pipeline.ExecuteAsync(ctx, CancellationToken.None));

        observer.ObservedFlagWhenRunning.ShouldBeTrue(
            customMessage: "Cleanup-phase IAlwaysRun step MUST observe ExecutionFailed=true after an " +
                           "upstream normal-phase step threw. Without this signal, DeployFailed-style " +
                           "conditional cleanup hooks can't tell success from failure.");
    }

    [Fact]
    public async Task ContextWithoutFailureAware_NotAffected_BackCompat()
    {
        // The interface is opt-in; legacy contexts that don't implement it
        // MUST keep their existing behaviour (pipeline doesn't blow up
        // trying to set a flag on a context that doesn't expose one).
        var ctx = new LegacyContext();
        var pipeline = new ExecutionPipeline<LegacyContext>(new IExecutionStep<LegacyContext>[]
        {
            new LegacyThrowingStep()
        });

        await Should.ThrowAsync<InvalidOperationException>(() =>
            pipeline.ExecuteAsync(ctx, CancellationToken.None));

        // Nothing else to assert — the lack of a runtime crash IS the test.
    }

    // ── Test stubs ──────────────────────────────────────────────────────────

    private sealed class FlagContext : IFailureAwareExecutionContext
    {
        public bool ExecutionFailed { get; set; }
    }

    private sealed class LegacyContext { /* no IFailureAware */ }

    private sealed class NoopStep : ExecutionStep<FlagContext>
    {
        public override Task ExecuteAsync(FlagContext context, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingStep : ExecutionStep<FlagContext>
    {
        private readonly string _message;
        public ThrowingStep(string message) { _message = message; }
        public override Task ExecuteAsync(FlagContext context, CancellationToken ct)
            => throw new InvalidOperationException(_message);
    }

    private sealed class LegacyThrowingStep : ExecutionStep<LegacyContext>
    {
        public override Task ExecuteAsync(LegacyContext context, CancellationToken ct)
            => throw new InvalidOperationException("legacy boom");
    }

    private sealed class FlagObserverAlwaysRun : IAlwaysRunExecutionStep<FlagContext>
    {
        public bool ObservedFlagWhenRunning { get; private set; }
        public bool IsEnabled(FlagContext context) => true;
        public Task ExecuteAsync(FlagContext context, CancellationToken ct)
        {
            ObservedFlagWhenRunning = context.ExecutionFailed;
            return Task.CompletedTask;
        }
    }
}
