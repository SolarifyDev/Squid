using System;
using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// Pins how the runner classifies a TRANSIENT INFRASTRUCTURE failure (a Halibut
/// RPC drop that outlived the library's own retries, or an unreachable agent):
/// by default it pauses the deployment for resume (OnTransientPauseAsync —
/// Paused + checkpoint preserved) rather than failing it terminally, so the
/// still-running script can be re-attached to. The historical fail-fast
/// behaviour remains behind the SQUID_DEPLOYMENT_TRANSIENT_RESUMABLE escape hatch.
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
    public async Task TransientFailure_FailFast_CallsOnFailure_NotOnTransientPause_AndRethrows()
    {
        // Escape hatch (SQUID_DEPLOYMENT_TRANSIENT_RESUMABLE=false): restore the
        // historical fail-fast behaviour — a transient drop routes through
        // OnFailureAsync (Failed + checkpoint deleted) and the runner rethrows.
        var runner = CreateFailFastRunner(CreateThrowingPhase(new HalibutClientException("connection reset by peer")));

        await Should.ThrowAsync<HalibutClientException>(() => runner.ProcessAsync(1, CancellationToken.None));

        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<HalibutClientException>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnTransientPauseAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenuineFailure_RoutesThroughOnFailure_NotTransientPause_AndRethrows()
    {
        // A non-transient exception (script/RBAC/logic failure) must NOT be mistaken
        // for a transient blip — it fails terminally and rethrows, even when
        // transient-resumable is on (the default).
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

    // ── Resumable-transient flag (Rule 8 env-var escape hatch) ────────────────

    [Fact]
    public void DeploymentTransientResumableEnvVar_ConstantNamePinned()
    {
        // Operators set this in Helm overrides / container env. Renaming it
        // silently flips every opted-out tenant back to resumable pausing.
        DeploymentPipelineRunner.DeploymentTransientResumableEnvVar
            .ShouldBe("SQUID_DEPLOYMENT_TRANSIENT_RESUMABLE");
    }

    [Fact]
    public void DefaultDeploymentTransientResumable_IsTrue()
    {
        DeploymentPipelineRunner.DefaultDeploymentTransientResumable.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null,    true)]
    [InlineData("",      true)]
    [InlineData("   ",   true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData(" off ", false)]
    [InlineData("0",     false)]
    [InlineData("no",    false)]
    [InlineData("true",  true)]
    [InlineData("1",     true)]
    [InlineData("garbage", true)]   // unrecognised → safe resumable default
    public void ParseTransientResumable_HandlesAllInputs(string raw, bool expected)
    {
        DeploymentPipelineRunner.ParseTransientResumable(raw).ShouldBe(expected);
    }

    [Fact]
    public void TransientResumableEnvVar_SetFalse_DrivesProperty()
    {
        var original = Environment.GetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTransientResumableEnvVar);
        Environment.SetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTransientResumableEnvVar, "false");

        try
        {
            var runner = new DeploymentPipelineRunner(Array.Empty<IDeploymentPipelinePhase>(), _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object);

            runner.TransientResumable.ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTransientResumableEnvVar, original);
        }
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
        => new(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object)
        {
            DeploymentTimeout = TimeSpan.FromMinutes(5),
            TransientResumable = true
        };

    private DeploymentPipelineRunner CreateFailFastRunner(params IDeploymentPipelinePhase[] phases)
        => new(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object)
        {
            DeploymentTimeout = TimeSpan.FromMinutes(5),
            TransientResumable = false
        };
}
