using System;
using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// Pins how the runner classifies a TRANSIENT INFRASTRUCTURE failure (a Halibut
/// RPC drop that outlived the library's own retries, or an unreachable agent):
/// it unconditionally pauses the deployment for resume (OnTransientPauseAsync —
/// Paused + checkpoint preserved) rather than failing it terminally, so the
/// still-running script can be re-attached to. There is no opt-out — failing fast
/// on a transient blip would discard progress and risk a duplicate run.
///
/// <para>Sibling to <c>DeploymentPipelineRunnerTimeoutTests</c>, which pins the
/// analogous wall-clock-timeout classification. A genuine (non-transient)
/// exception must still route through OnFailureAsync + rethrow — pinned here so a
/// future regression that over-broadens the transient predicate is caught.</para>
/// </summary>
public class DeploymentPipelineRunnerTransientPauseTests
{
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly TaskCancellationRegistry _registry = new();
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();

    [Theory]
    [InlineData("halibut")]   // HalibutClientException — RPC drop after library retries
    [InlineData("agent")]     // AgentUnreachableException — liveness probe gave up
    public async Task TransientFailure_Default_CallsOnTransientPause_NotOnFailure(string kind)
    {
        var runner = CreateRunner(CreateThrowingPhase(TransientOf(kind)));

        await runner.ProcessAsync(1, CancellationToken.None);

        _completion.Verify(c => c.OnTransientPauseAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TransientFailure_Default_EmitsPausedEvent_AndDoesNotRethrow()
    {
        var runner = CreateRunner(CreateThrowingPhase(new HalibutClientException("connection reset by peer")));

        await Should.NotThrowAsync(() => runner.ProcessAsync(1, CancellationToken.None));

        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentPausedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentFailedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenuineFailure_RoutesThroughOnFailure_NotTransientPause_AndRethrows()
    {
        // A non-transient exception (script/RBAC/logic failure) must NOT be mistaken
        // for a transient blip — it fails terminally and rethrows.
        var runner = CreateRunner(CreateThrowingPhase(new InvalidOperationException("a real failure")));

        await Should.ThrowAsync<InvalidOperationException>(() => runner.ProcessAsync(1, CancellationToken.None));

        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<InvalidOperationException>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnTransientPauseAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AggregateOfTransients_PausesForResume()
    {
        // A parallel batch surfaces multiple target failures as an AggregateException.
        // When EVERY inner failure is transient, the whole deployment pauses.
        var aggregate = new AggregateException(new HalibutClientException("drop A"), new HalibutClientException("drop B"));
        var runner = CreateRunner(CreateThrowingPhase(aggregate));

        await runner.ProcessAsync(1, CancellationToken.None);

        _completion.Verify(c => c.OnTransientPauseAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AggregateWithOneGenuineFailure_FailsNotPauses()
    {
        // A mix that includes ANY real failure is a true failure, not a pausable blip.
        var aggregate = new AggregateException(new HalibutClientException("drop"), new InvalidOperationException("real"));
        var runner = CreateRunner(CreateThrowingPhase(aggregate));

        await Should.ThrowAsync<AggregateException>(() => runner.ProcessAsync(1, CancellationToken.None));

        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnTransientPauseAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TransientFailure_DuringUserCancel_DoesNotPause()
    {
        // A user-cancel that races a transient RPC drop (a raw HalibutClientException,
        // NOT an OperationCanceledException) must NOT be reclassified as a transient
        // pause — the cancel/timeout guard makes cancel win, so the transient catch is
        // skipped and the exception falls through to the failure/cancel handling.
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentTaskContext, CancellationToken>((_, __) =>
            {
                _registry.TryCancel(1);
                throw new HalibutClientException("connection reset while cancelling");
            });
        var runner = CreateRunner(phase.Object);

        await Should.ThrowAsync<HalibutClientException>(() => runner.ProcessAsync(1, CancellationToken.None));

        _completion.Verify(c => c.OnTransientPauseAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never,
            failMessage: "A transient drop racing a user-cancel must NOT pause — cancel/timeout win over the transient classification.");
    }

    private static Exception TransientOf(string kind)
        => kind == "agent" ? new AgentUnreachableException("agent-1", 3) : new HalibutClientException("connection reset by peer");

    private IDeploymentPipelinePhase CreateThrowingPhase(Exception ex)
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);

        return phase.Object;
    }

    // Long timeout so the wall-clock timer never fires before the phase throws.
    private DeploymentPipelineRunner CreateRunner(params IDeploymentPipelinePhase[] phases)
        => new(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>())
        {
            DeploymentTimeout = TimeSpan.FromMinutes(5)
        };
}
