using System;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class ScriptExecutionPlanFactoryTests
{
    private static ScriptExecutionRequest CreateRequest(ExecutionMode mode) => new()
    {
        ExecutionMode = mode,
        ScriptBody = "echo 'test'"
    };

    [Fact]
    public void Create_DirectScriptMode_ReturnsDirectScriptExecutionPlan()
    {
        var request = CreateRequest(ExecutionMode.DirectScript);

        var result = ScriptExecutionPlanFactory.Create(request);

        result.ShouldBeOfType<DirectScriptExecutionPlan>();
        result.Mode.ShouldBe(ExecutionMode.DirectScript);
        result.Request.ShouldBe(request);
    }

    [Fact]
    public void Create_PackagedPayloadMode_ReturnsPackagedPayloadExecutionPlan()
    {
        var request = CreateRequest(ExecutionMode.PackagedPayload);

        var result = ScriptExecutionPlanFactory.Create(request);

        result.ShouldBeOfType<PackagedPayloadExecutionPlan>();
        result.Mode.ShouldBe(ExecutionMode.PackagedPayload);
        result.Request.ShouldBe(request);
    }

    [Fact]
    public void Create_NullRequest_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => ScriptExecutionPlanFactory.Create(null));
    }

    [Fact]
    public void Create_UnspecifiedMode_ThrowsInvalidOperationException()
    {
        var request = CreateRequest(ExecutionMode.Unspecified);

        Should.Throw<InvalidOperationException>(() => ScriptExecutionPlanFactory.Create(request));
    }

    [Fact]
    public void Create_UnsupportedMode_ThrowsInvalidOperationException()
    {
        var request = CreateRequest(ExecutionMode.ManualIntervention);

        var ex = Should.Throw<InvalidOperationException>(() => ScriptExecutionPlanFactory.Create(request));

        ex.Message.ShouldContain("ManualIntervention");
    }
}
