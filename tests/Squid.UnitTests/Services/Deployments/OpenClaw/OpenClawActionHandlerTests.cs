using System.Linq;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.OpenClaw;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.OpenClaw;

public class OpenClawActionHandlerTests
{
    private static ActionExecutionContext MakeContext(Dictionary<string, string> properties = null)
    {
        var propList = (properties ?? new()).Select(kv => new DeploymentActionPropertyDto
        {
            PropertyName = kv.Key,
            PropertyValue = kv.Value
        }).ToList();

        return new ActionExecutionContext
        {
            Step = new DeploymentStepDto { Name = "Step1" },
            Action = new DeploymentActionDto
            {
                Name = "Action1",
                ActionType = "Squid.OpenClaw.InvokeTool",
                Properties = propList
            },
            Variables = new(),
            ReleaseVersion = "1.0.0"
        };
    }

    // ========================================================================
    // InvokeTool
    // ========================================================================

    [Fact]
    public async Task InvokeTool_SetsActionKindAndTool()
    {
        var handler = new OpenClawInvokeToolActionHandler();
        var ctx = MakeContext(new()
        {
            ["Squid.Action.OpenClaw.Tool"] = "sessions_list",
            ["Squid.Action.OpenClaw.ToolAction"] = "json",
            ["Squid.Action.OpenClaw.ArgsJson"] = """{"key":"value"}""",
            ["Squid.Action.OpenClaw.SessionKey"] = "main"
        });

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Skip);
        result.ActionProperties["OpenClaw.ActionKind"].ShouldBe("InvokeTool");
        result.ActionProperties["OpenClaw.Tool"].ShouldBe("sessions_list");
        result.ActionProperties["OpenClaw.ToolAction"].ShouldBe("json");
        result.ActionProperties["OpenClaw.SessionKey"].ShouldBe("main");
    }

    [Fact]
    public void InvokeTool_ActionType_MatchesConstant()
    {
        new OpenClawInvokeToolActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawInvokeTool);
    }

    // ========================================================================
    // RunAgent
    // ========================================================================

    [Fact]
    public async Task RunAgent_SetsAllProperties()
    {
        var handler = new OpenClawRunAgentActionHandler();
        var ctx = MakeContext(new()
        {
            ["Squid.Action.OpenClaw.Message"] = "Run task",
            ["Squid.Action.OpenClaw.AgentId"] = "hooks",
            ["Squid.Action.OpenClaw.SessionKey"] = "hook:test",
            ["Squid.Action.OpenClaw.WakeMode"] = "now",
            ["Squid.Action.OpenClaw.Deliver"] = "true",
            ["Squid.Action.OpenClaw.Channel"] = "last",
            ["Squid.Action.OpenClaw.To"] = "+15551234567"
        });

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ActionProperties["OpenClaw.ActionKind"].ShouldBe("RunAgent");
        result.ActionProperties["OpenClaw.Message"].ShouldBe("Run task");
        result.ActionProperties["OpenClaw.AgentId"].ShouldBe("hooks");
        result.ActionProperties["OpenClaw.Deliver"].ShouldBe("true");
        result.ActionProperties["OpenClaw.Channel"].ShouldBe("last");
        result.ActionProperties["OpenClaw.To"].ShouldBe("+15551234567");
    }

    [Fact]
    public void RunAgent_ActionType_MatchesConstant()
    {
        new OpenClawRunAgentActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawRunAgent);
    }

    // ========================================================================
    // Wake
    // ========================================================================

    [Fact]
    public async Task Wake_SetsTextAndMode()
    {
        var handler = new OpenClawWakeActionHandler();
        var ctx = MakeContext(new()
        {
            ["Squid.Action.OpenClaw.WakeText"] = "System event",
            ["Squid.Action.OpenClaw.WakeMode"] = "next-heartbeat"
        });

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ActionProperties["OpenClaw.ActionKind"].ShouldBe("Wake");
        result.ActionProperties["OpenClaw.WakeText"].ShouldBe("System event");
        result.ActionProperties["OpenClaw.WakeMode"].ShouldBe("next-heartbeat");
    }

    [Fact]
    public void Wake_ActionType_MatchesConstant()
    {
        new OpenClawWakeActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawWake);
    }

    // ========================================================================
    // WaitSession
    // ========================================================================

    [Fact]
    public async Task WaitSession_SetsPollingConfig()
    {
        var handler = new OpenClawWaitSessionActionHandler();
        var ctx = MakeContext(new()
        {
            ["Squid.Action.OpenClaw.SessionKey"] = "hook:test",
            ["Squid.Action.OpenClaw.SuccessPattern"] = "completed",
            ["Squid.Action.OpenClaw.FailPattern"] = "error",
            ["Squid.Action.OpenClaw.MaxWaitSeconds"] = "60",
            ["Squid.Action.OpenClaw.PollSeconds"] = "10"
        });

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ActionProperties["OpenClaw.ActionKind"].ShouldBe("WaitSession");
        result.ActionProperties["OpenClaw.SessionKey"].ShouldBe("hook:test");
        result.ActionProperties["OpenClaw.SuccessPattern"].ShouldBe("completed");
        result.ActionProperties["OpenClaw.MaxWaitSeconds"].ShouldBe("60");
        result.ActionProperties["OpenClaw.PollSeconds"].ShouldBe("10");
    }

    [Fact]
    public async Task WaitSession_DefaultsPollingValues()
    {
        var handler = new OpenClawWaitSessionActionHandler();
        var ctx = MakeContext(new() { ["Squid.Action.OpenClaw.SessionKey"] = "test" });

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ActionProperties["OpenClaw.MaxWaitSeconds"].ShouldBe("120");
        result.ActionProperties["OpenClaw.PollSeconds"].ShouldBe("5");
    }

    [Fact]
    public void WaitSession_ActionType_MatchesConstant()
    {
        new OpenClawWaitSessionActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawWaitSession);
    }

    // ========================================================================
    // Assert
    // ========================================================================

    [Fact]
    public async Task Assert_SetsAssertionConfig()
    {
        var handler = new OpenClawAssertActionHandler();
        var ctx = MakeContext(new()
        {
            ["Squid.Action.OpenClaw.JsonPath"] = "$.result.ok",
            ["Squid.Action.OpenClaw.Operator"] = "equals",
            ["Squid.Action.OpenClaw.Expected"] = "true"
        });

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ActionProperties["OpenClaw.ActionKind"].ShouldBe("Assert");
        result.ActionProperties["OpenClaw.JsonPath"].ShouldBe("$.result.ok");
        result.ActionProperties["OpenClaw.Operator"].ShouldBe("equals");
        result.ActionProperties["OpenClaw.Expected"].ShouldBe("true");
    }

    [Fact]
    public async Task Assert_DefaultsToResultJsonSource()
    {
        var handler = new OpenClawAssertActionHandler();
        var ctx = MakeContext(new() { ["Squid.Action.OpenClaw.JsonPath"] = "$.ok" });

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ActionProperties["OpenClaw.SourceVariable"].ShouldBe(SpecialVariables.OpenClaw.ResultJson);
    }

    [Fact]
    public void Assert_ActionType_MatchesConstant()
    {
        new OpenClawAssertActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawAssert);
    }

    // ========================================================================
    // FetchResult
    // ========================================================================

    [Fact]
    public async Task FetchResult_SetsFieldMappings()
    {
        var handler = new OpenClawFetchResultActionHandler();
        var mappings = """[{"jsonPath":"$.result.summary","outputName":"Summary"}]""";
        var ctx = MakeContext(new()
        {
            ["Squid.Action.OpenClaw.FieldMappings"] = mappings
        });

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ActionProperties["OpenClaw.ActionKind"].ShouldBe("FetchResult");
        result.ActionProperties["OpenClaw.FieldMappings"].ShouldBe(mappings);
        result.ActionProperties["OpenClaw.SourceVariable"].ShouldBe(SpecialVariables.OpenClaw.ResultJson);
    }

    [Fact]
    public void FetchResult_ActionType_MatchesConstant()
    {
        new OpenClawFetchResultActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawFetchResult);
    }

    // ========================================================================
    // All handlers — shared assertions
    // ========================================================================

    [Theory]
    [InlineData(typeof(OpenClawInvokeToolActionHandler))]
    [InlineData(typeof(OpenClawRunAgentActionHandler))]
    [InlineData(typeof(OpenClawWakeActionHandler))]
    [InlineData(typeof(OpenClawWaitSessionActionHandler))]
    [InlineData(typeof(OpenClawAssertActionHandler))]
    [InlineData(typeof(OpenClawFetchResultActionHandler))]
    public async Task AllHandlers_ReturnDirectScriptWithSkipContext(Type handlerType)
    {
        var handler = (IActionHandler)Activator.CreateInstance(handlerType);
        var ctx = MakeContext();

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Skip);
        result.PayloadKind.ShouldBe(PayloadKind.None);
    }
}
