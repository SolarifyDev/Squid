using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class StepTimeoutParserTests
{
    [Fact]
    public void ParseTimeout_ValidTimeSpan_ReturnsParsed()
    {
        var step = BuildStepWithTimeout("00:15:00");

        var result = StepTimeoutParser.ParseTimeout(step);

        result.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseTimeout_NullOrEmpty_ReturnsNull(string value)
    {
        var step = value == null
            ? new DeploymentStepDto { Properties = new List<DeploymentStepPropertyDto>() }
            : BuildStepWithTimeout(value);

        var result = StepTimeoutParser.ParseTimeout(step);

        result.ShouldBeNull();
    }

    [Theory]
    [InlineData("not-a-timespan")]
    [InlineData("-00:05:00")]
    public void ParseTimeout_InvalidOrNegative_ReturnsNull(string value)
    {
        var step = BuildStepWithTimeout(value);

        var result = StepTimeoutParser.ParseTimeout(step);

        result.ShouldBeNull();
    }

    [Fact]
    public void ParseTimeout_NoProperties_ReturnsNull()
    {
        var step = new DeploymentStepDto { Properties = null };

        var result = StepTimeoutParser.ParseTimeout(step);

        result.ShouldBeNull();
    }

    private static DeploymentStepDto BuildStepWithTimeout(string value)
    {
        return new DeploymentStepDto
        {
            Properties = new List<DeploymentStepPropertyDto>
            {
                new() { PropertyName = SpecialVariables.Step.Timeout, PropertyValue = value }
            }
        };
    }
}
