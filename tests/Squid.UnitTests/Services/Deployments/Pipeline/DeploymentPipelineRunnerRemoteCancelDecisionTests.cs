using System;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// Pins the <see cref="RemoteScriptCancelDecision"/> the runner hands to the phases (via
/// <see cref="DeploymentTaskContext.ShouldCancelRemoteScript"/>): an in-flight remote script is
/// cancelled on the agent only when the interruption sends the task to a terminal state. A paused task
/// (resumable timeout, host shutdown) keeps its in-flight pointer for resume, so killing the script
/// would make resume re-attach to a cancelled script.
/// </summary>
public class DeploymentPipelineRunnerRemoteCancelDecisionTests
{
    public enum Interruption { InternalFailFast, UserCancel, UserCancelAndExternal, TimeoutResumable, TimeoutFailFast, ExternalCancel }

    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly TaskCancellationRegistry _registry = new();
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();

    [Theory]
    [InlineData(Interruption.InternalFailFast, true)]        // unrecognised internal source (required-step peer abort): legacy cancel
    [InlineData(Interruption.UserCancel, true)]
    [InlineData(Interruption.UserCancelAndExternal, true)]   // user cancel wins over a concurrent shutdown: task is terminal
    [InlineData(Interruption.TimeoutResumable, false)]
    [InlineData(Interruption.TimeoutFailFast, true)]
    [InlineData(Interruption.ExternalCancel, false)]
    public async Task Decision_WhenInterrupted_CancelsRemoteScriptOnlyIfTaskHeadsTerminal(Interruption interruption, bool expectedCancel)
    {
        using var external = new CancellationTokenSource();
        bool? decisionAtInterruption = null;
        var phase = new InterruptingPhase(interruption, _registry, external, d => decisionAtInterruption = d);
        var runner = CreateRunner(phase, resumable: interruption != Interruption.TimeoutFailFast);

        // An internal fail-fast cancellation is not a runner-recognised source, so the runner treats the
        // resulting OperationCanceledException as a failure and rethrows; a host shutdown is rethrown so
        // Hangfire re-delivers the job. Neither rethrow is under test here.
        try { await runner.ProcessAsync(1, external.Token); }
        catch (OperationCanceledException) when (interruption is Interruption.InternalFailFast or Interruption.ExternalCancel) { }

        decisionAtInterruption.ShouldBe(expectedCancel,
            customMessage: $"{interruption}: the decision the observer sees when its token fires must be {expectedCancel}. " +
                           "False only for a recognised pause (resumable timeout, external/host cancellation) where a resume must " +
                           "re-attach to the still-running script; true for everything else, including cancellation sources the runner " +
                           "does not know about (their task ends terminal, so a surviving script would be orphaned).");
    }

    [Fact]
    public async Task Decision_IsHandedToPhasesOnContext_SoItReachesDispatch()
    {
        using var external = new CancellationTokenSource();
        RemoteScriptCancelDecision seen = null;
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentTaskContext, CancellationToken>((ctx, _) => seen = ctx.ShouldCancelRemoteScript)
            .Returns(Task.CompletedTask);
        var runner = CreateRunner(phase.Object, resumable: true);

        await runner.ProcessAsync(1, external.Token);

        seen.ShouldNotBeNull(customMessage: "The runner must put its decision on the context; a null decision means legacy always-cancel.");
    }

    private DeploymentPipelineRunner CreateRunner(IDeploymentPipelinePhase phase, bool resumable)
        => new(new[] { phase }, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>())
        {
            DeploymentTimeout = TimeSpan.FromMilliseconds(100),
            TimeoutResumable = resumable
        };

    // Plays the part of the observer: blocks on the runner's token, and when it fires evaluates the
    // decision exactly as the observer's OperationCanceledException handler does.
    private sealed class InterruptingPhase : IDeploymentPipelinePhase
    {
        private readonly Interruption _interruption;
        private readonly TaskCancellationRegistry _registry;
        private readonly CancellationTokenSource _external;
        private readonly Action<bool> _record;

        public InterruptingPhase(Interruption interruption, TaskCancellationRegistry registry, CancellationTokenSource external, Action<bool> record)
        {
            _interruption = interruption;
            _registry = registry;
            _external = external;
            _record = record;
        }

        public int Order => 1;

        public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
        {
            using var peer = CancellationTokenSource.CreateLinkedTokenSource(ct);

            if (_interruption is Interruption.UserCancel or Interruption.UserCancelAndExternal) _registry.TryCancel(ctx.ServerTaskId);
            if (_interruption is Interruption.ExternalCancel or Interruption.UserCancelAndExternal) _external.Cancel();
            if (_interruption == Interruption.InternalFailFast) peer.Cancel();

            try
            {
                await Task.Delay(Timeout.Infinite, peer.Token);
            }
            catch (OperationCanceledException)
            {
                _record(ctx.ShouldCancelRemoteScript());
                throw;
            }
        }
    }
}
