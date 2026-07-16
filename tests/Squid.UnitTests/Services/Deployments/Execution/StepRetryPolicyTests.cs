using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class StepRetryPolicyTests
{
    [Fact]
    public void FromStep_Disabled_ReturnsSingleAttempt()
    {
        var step = new DeploymentStepDto { Properties = new() };

        var policy = StepRetryPolicy.FromStep(step);

        policy.Enabled.ShouldBeFalse();
        policy.MaxAttempts.ShouldBe(1);
    }

    [Fact]
    public void FromStep_MissingProperties_ReturnsSingleAttempt()
    {
        var step = new DeploymentStepDto { Properties = null };

        var policy = StepRetryPolicy.FromStep(step);

        policy.Enabled.ShouldBeFalse();
        policy.MaxAttempts.ShouldBe(1);
    }

    [Fact]
    public void FromStep_EnabledCount2_Returns3Attempts()
    {
        var step = new DeploymentStepDto
        {
            Properties = new List<DeploymentStepPropertyDto>
            {
                new() { PropertyName = SpecialVariables.Step.RetriesEnabled, PropertyValue = "true" },
                new() { PropertyName = SpecialVariables.Step.RetriesCount, PropertyValue = "2" },
            }
        };

        var policy = StepRetryPolicy.FromStep(step);

        policy.Enabled.ShouldBeTrue();
        policy.MaxAttempts.ShouldBe(3);
    }

    [Theory]
    [InlineData("0", 2)]
    [InlineData("9", 4)]
    public void FromStep_ClampsRetryCount(string raw, int expectedAttempts)
    {
        var step = new DeploymentStepDto
        {
            Properties = new List<DeploymentStepPropertyDto>
            {
                new() { PropertyName = SpecialVariables.Step.RetriesEnabled, PropertyValue = "true" },
                new() { PropertyName = SpecialVariables.Step.RetriesCount, PropertyValue = raw },
            }
        };

        StepRetryPolicy.FromStep(step).MaxAttempts.ShouldBe(expectedAttempts);
    }

    [Theory]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData("tRuE")]
    public void FromStep_EnabledCaseInsensitive(string value)
    {
        var step = new DeploymentStepDto
        {
            Properties = new List<DeploymentStepPropertyDto>
            {
                new() { PropertyName = SpecialVariables.Step.RetriesEnabled, PropertyValue = value },
                new() { PropertyName = SpecialVariables.Step.RetriesCount, PropertyValue = "1" },
            }
        };

        var policy = StepRetryPolicy.FromStep(step);

        policy.Enabled.ShouldBeTrue();
        policy.MaxAttempts.ShouldBe(2);
    }

    [Fact]
    public void FromStep_EnabledMissingCount_DefaultsToOneRetry()
    {
        var step = new DeploymentStepDto
        {
            Properties = new List<DeploymentStepPropertyDto>
            {
                new() { PropertyName = SpecialVariables.Step.RetriesEnabled, PropertyValue = "true" },
            }
        };

        var policy = StepRetryPolicy.FromStep(step);

        policy.Enabled.ShouldBeTrue();
        policy.MaxAttempts.ShouldBe(2);
    }

    [Fact]
    public void IsRetryable_DeploymentScriptException_ReturnsTrue()
    {
        StepRetryPolicy.IsRetryable(new DeploymentScriptException("script failed"), CancellationToken.None)
            .ShouldBeTrue();
    }

    [Fact]
    public void IsRetryable_GeneralException_ReturnsTrue()
    {
        StepRetryPolicy.IsRetryable(new InvalidOperationException("boom"), CancellationToken.None)
            .ShouldBeTrue();
    }

    [Fact]
    public void IsRetryable_DeploymentAbortedException_ReturnsFalse()
    {
        StepRetryPolicy.IsRetryable(new DeploymentAbortedException("aborted"), CancellationToken.None)
            .ShouldBeFalse();
    }

    [Fact]
    public void IsRetryable_OperationCanceledWhenTokenCancelled_ReturnsFalse()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        StepRetryPolicy.IsRetryable(new OperationCanceledException(cts.Token), cts.Token)
            .ShouldBeFalse();
    }

    [Fact]
    public void IsRetryable_OperationCanceledWhenTokenNotCancelled_ReturnsFalse()
    {
        StepRetryPolicy.IsRetryable(new OperationCanceledException(), CancellationToken.None)
            .ShouldBeFalse();
    }

    [Fact]
    public void IsRetryable_TransientAgentUnreachable_ReturnsFalse()
    {
        StepRetryPolicy.IsRetryable(new AgentUnreachableException("agent-1", 3), CancellationToken.None)
            .ShouldBeFalse();
    }

    [Fact]
    public void IsRetryable_TransientHalibutClientException_ReturnsFalse()
    {
        StepRetryPolicy.IsRetryable(new HalibutClientException("connection reset by peer"), CancellationToken.None)
            .ShouldBeFalse();
    }

    [Fact]
    public void Constants_AreStable()
    {
        SpecialVariables.Step.RetriesEnabled.ShouldBe("Squid.Step.RetriesEnabled");
        SpecialVariables.Step.RetriesCount.ShouldBe("Squid.Step.RetriesCount");
    }
}
