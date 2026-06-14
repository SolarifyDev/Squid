using System;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentPipelineRunnerTimeoutTests
{
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly TaskCancellationRegistry _registry = new();
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();

    [Fact]
    public async Task Timeout_Default_CallsOnTimedOut_WithDeploymentTimeoutException()
    {
        // Default (resumable) behaviour: a timeout pauses + preserves the
        // checkpoint via OnTimedOutAsync, NOT OnFailureAsync (which would delete
        // the checkpoint and fail the task irrecoverably).
        var phase = CreateHangingPhase();
        var runner = CreateRunner(phase);

        await runner.ProcessAsync(1, CancellationToken.None);

        _completion.Verify(c => c.OnTimedOutAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<DeploymentTimeoutException>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<DeploymentTimeoutException>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Timeout_FailFast_CallsOnFailure_NotOnTimedOut()
    {
        // Escape hatch (SQUID_DEPLOYMENT_TIMEOUT_RESUMABLE=false): restore the
        // historical fail-fast behaviour — a timeout routes through OnFailureAsync
        // (Failed + checkpoint deleted), never OnTimedOutAsync.
        var phase = CreateHangingPhase();
        var runner = CreateFailFastRunner(phase);

        await runner.ProcessAsync(1, CancellationToken.None);

        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<DeploymentTimeoutException>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnTimedOutAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<DeploymentTimeoutException>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]    // resumable (default)
    [InlineData(false)]   // fail-fast (escape hatch)
    public async Task Timeout_AlwaysEmitsDeploymentTimedOutEvent(bool resumable)
    {
        // The lifecycle signal "this deployment timed out" must fire regardless of
        // how the terminal/pause state is recorded — observers keying off the
        // event see the timeout in both modes.
        var phase = CreateHangingPhase();
        var runner = resumable ? CreateRunner(phase) : CreateFailFastRunner(phase);

        await runner.ProcessAsync(1, CancellationToken.None);

        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentTimedOutEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentCancelledEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Timeout_DoesNotRethrow()
    {
        var phase = CreateHangingPhase();
        var runner = CreateRunner(phase);

        await Should.NotThrowAsync(() => runner.ProcessAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_Unregisters()
    {
        var phase = CreateHangingPhase();
        var runner = CreateRunner(phase);

        await runner.ProcessAsync(1, CancellationToken.None);

        _registry.TryCancel(1).ShouldBeFalse();
    }

    [Fact]
    public async Task UserCancel_DuringTimeout_TreatedAsCancellation()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentTaskContext, CancellationToken>(async (_, ct) =>
            {
                _registry.TryCancel(1);
                await Task.Delay(Timeout.Infinite, ct);
            });
        var runner = CreateRunner(phase.Object);

        await runner.ProcessAsync(1, CancellationToken.None);

        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<DeploymentTimeoutException>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnTimedOutAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<DeploymentTimeoutException>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Configurable deployment timeout (Rule 8 env-var escape hatch) ─────────
    // Long-running deployments (large DB migrations, multi-stage rollouts) can
    // legitimately exceed the 60-min default. Before this knob the timer was a
    // hardcoded internal-init constant with no operator override, so any such
    // deployment was killed at 60 min. The env var lets operators raise it
    // without a code change; default behaviour (60 min when unset) is unchanged.

    [Fact]
    public void DeploymentTimeoutMinutesEnvVar_ConstantNamePinned()
    {
        // Operators set this in Helm overrides / container env. Renaming it
        // silently reverts every tuned tenant back to the 60-min default.
        DeploymentPipelineRunner.DeploymentTimeoutMinutesEnvVar
            .ShouldBe("SQUID_DEPLOYMENT_TIMEOUT_MINUTES");
    }

    [Fact]
    public void DefaultDeploymentTimeoutMinutes_Is60()
    {
        DeploymentPipelineRunner.DefaultDeploymentTimeoutMinutes.ShouldBe(60);
    }

    [Theory]
    [InlineData(null,        60)]   // unset → default
    [InlineData("",          60)]   // empty → default
    [InlineData("   ",       60)]   // whitespace → default
    [InlineData("90",        90)]   // explicit override
    [InlineData(" 120 ",    120)]   // surrounding whitespace trimmed
    [InlineData("+90",       90)]   // leading sign accepted by int.TryParse
    [InlineData("0060",      60)]   // leading zeros accepted
    [InlineData("240",      240)]   // larger override (4h migration)
    [InlineData("60",        60)]   // explicit value equal to default still parses
    [InlineData("not-int",   60)]   // garbage → default + warn
    [InlineData("12.5",      60)]   // non-integer → default
    [InlineData("0",         60)]   // zero would disable the safety timer → default
    [InlineData("-5",        60)]   // negative → default
    public void ParseDeploymentTimeout_HandlesAllInputs(string raw, int expectedMinutes)
    {
        DeploymentPipelineRunner.ParseDeploymentTimeout(raw)
            .ShouldBe(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void EnvVar_Unset_EffectiveTimeoutIs60Min()
    {
        var original = Environment.GetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTimeoutMinutesEnvVar);
        Environment.SetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTimeoutMinutesEnvVar, null);

        try
        {
            var runner = CreateRunnerWithoutTimeoutOverride();

            runner.DeploymentTimeout.ShouldBe(TimeSpan.FromMinutes(60));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTimeoutMinutesEnvVar, original);
        }
    }

    [Fact]
    public void EnvVar_Set_DrivesEffectiveDeploymentTimeout()
    {
        // 123 min: distinct from the 60-min default (proves env→property wiring)
        // AND far longer than any unit test's runtime, so a parallel no-override
        // runner construction that briefly reads this value can never time out.
        var original = Environment.GetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTimeoutMinutesEnvVar);
        Environment.SetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTimeoutMinutesEnvVar, "123");

        try
        {
            var runner = CreateRunnerWithoutTimeoutOverride();

            runner.DeploymentTimeout.ShouldBe(TimeSpan.FromMinutes(123));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTimeoutMinutesEnvVar, original);
        }
    }

    // ── Resumable-timeout flag (Rule 8 env-var escape hatch) ──────────────────
    // On timeout the safe default pauses + preserves the checkpoint (resumable).
    // Operators who alert on the terminal Failed state can opt back into the
    // historical fail-fast behaviour via this env var. Default (unset) is
    // resumable; only an explicit falsey token disables it.

    [Fact]
    public void DeploymentTimeoutResumableEnvVar_ConstantNamePinned()
    {
        // Operators set this in Helm overrides / container env. Renaming it
        // silently flips every opted-out tenant back to resumable pausing.
        DeploymentPipelineRunner.DeploymentTimeoutResumableEnvVar
            .ShouldBe("SQUID_DEPLOYMENT_TIMEOUT_RESUMABLE");
    }

    [Fact]
    public void DefaultDeploymentTimeoutResumable_IsTrue()
    {
        DeploymentPipelineRunner.DefaultDeploymentTimeoutResumable.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null,    true)]    // unset → resumable default
    [InlineData("",      true)]    // empty → default
    [InlineData("   ",   true)]    // whitespace → default
    [InlineData("false", false)]   // explicit opt-out
    [InlineData("False", false)]   // case-insensitive
    [InlineData("FALSE", false)]   // case-insensitive
    [InlineData(" false ", false)] // surrounding whitespace trimmed
    [InlineData("0",     false)]   // numeric opt-out
    [InlineData("no",    false)]   // common falsey token
    [InlineData("off",   false)]   // common falsey token
    [InlineData("OFF",   false)]   // case-insensitive
    [InlineData("true",  true)]    // explicit resumable
    [InlineData("1",     true)]    // numeric truthy
    [InlineData("yes",   true)]    // truthy token
    [InlineData("garbage", true)]  // unrecognised → safe resumable default
    public void ParseTimeoutResumable_HandlesAllInputs(string raw, bool expected)
    {
        DeploymentPipelineRunner.ParseTimeoutResumable(raw).ShouldBe(expected);
    }

    [Fact]
    public void ResumableEnvVar_Unset_TimeoutResumableIsTrue()
    {
        var original = Environment.GetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTimeoutResumableEnvVar);
        Environment.SetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTimeoutResumableEnvVar, null);

        try
        {
            var runner = CreateRunnerWithoutTimeoutOverride();

            runner.TimeoutResumable.ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTimeoutResumableEnvVar, original);
        }
    }

    [Fact]
    public void ResumableEnvVar_SetFalse_DrivesTimeoutResumableProperty()
    {
        var original = Environment.GetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTimeoutResumableEnvVar);
        Environment.SetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTimeoutResumableEnvVar, "false");

        try
        {
            var runner = CreateRunnerWithoutTimeoutOverride();

            runner.TimeoutResumable.ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DeploymentPipelineRunner.DeploymentTimeoutResumableEnvVar, original);
        }
    }

    private DeploymentPipelineRunner CreateRunnerWithoutTimeoutOverride()
    {
        return new DeploymentPipelineRunner(Array.Empty<IDeploymentPipelinePhase>(), _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());
    }

    private IDeploymentPipelinePhase CreateHangingPhase()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentTaskContext, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
            });

        return phase.Object;
    }

    private DeploymentPipelineRunner CreateRunner(params IDeploymentPipelinePhase[] phases)
    {
        return new DeploymentPipelineRunner(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>())
        {
            DeploymentTimeout = TimeSpan.FromMilliseconds(50),
            TimeoutResumable = true
        };
    }

    private DeploymentPipelineRunner CreateFailFastRunner(params IDeploymentPipelinePhase[] phases)
    {
        return new DeploymentPipelineRunner(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>())
        {
            DeploymentTimeout = TimeSpan.FromMilliseconds(50),
            TimeoutResumable = false
        };
    }
}
