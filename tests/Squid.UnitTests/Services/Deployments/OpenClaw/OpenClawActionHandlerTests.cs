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
    public async Task InvokeTool_SetsExecutionSemantics()
    {
        var handler = new OpenClawInvokeToolActionHandler();
        var ctx = MakeContext(new()
        {
            [SpecialVariables.OpenClaw.PropTool] = "sessions_list",
            [SpecialVariables.OpenClaw.PropToolAction] = "json"
        });

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Skip);
        result.ScriptBody.ShouldContain("sessions_list");
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
    public async Task RunAgent_SetsExecutionSemantics()
    {
        var handler = new OpenClawRunAgentActionHandler();
        var ctx = MakeContext(new()
        {
            [SpecialVariables.OpenClaw.PropMessage] = "Run task"
        });

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Skip);
        result.ScriptBody.ShouldContain("Run task");
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
    public async Task Wake_SetsExecutionSemantics()
    {
        var handler = new OpenClawWakeActionHandler();
        var ctx = MakeContext(new()
        {
            [SpecialVariables.OpenClaw.PropWakeText] = "System event"
        });

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Skip);
        result.ScriptBody.ShouldContain("System event");
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
    public async Task WaitSession_SetsExecutionSemantics()
    {
        var handler = new OpenClawWaitSessionActionHandler();
        var ctx = MakeContext(new()
        {
            [SpecialVariables.OpenClaw.PropSessionKey] = "hook:test"
        });

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Skip);
        result.ScriptBody.ShouldContain("hook:test");
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
    public async Task Assert_SetsExecutionSemantics()
    {
        var handler = new OpenClawAssertActionHandler();
        var ctx = MakeContext();

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Skip);
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
    public async Task FetchResult_SetsExecutionSemantics()
    {
        var handler = new OpenClawFetchResultActionHandler();
        var ctx = MakeContext();

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Skip);
    }

    [Fact]
    public void FetchResult_ActionType_MatchesConstant()
    {
        new OpenClawFetchResultActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawFetchResult);
    }

    // ========================================================================
    // ChatCompletion
    // ========================================================================

    [Fact]
    public async Task ChatCompletion_SetsExecutionSemantics()
    {
        var handler = new OpenClawChatCompletionActionHandler();
        var ctx = MakeContext(new()
        {
            [SpecialVariables.OpenClaw.PropPrompt] = "Summarize the release"
        });

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Skip);
        result.ScriptBody.ShouldContain("Summarize the release");
    }

    [Fact]
    public void ChatCompletion_ActionType_MatchesConstant()
    {
        new OpenClawChatCompletionActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawChatCompletion);
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
    [InlineData(typeof(OpenClawChatCompletionActionHandler))]
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
