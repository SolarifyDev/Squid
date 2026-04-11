using System.Linq;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.OpenClaw;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.OpenClaw;

/// <summary>
/// Phase 9f — verifies that every OpenClaw <see cref="IActionHandler"/> overrides
/// <c>DescribeIntentAsync</c> and emits an <see cref="OpenClawInvokeIntent"/> directly,
/// with a stable semantic name (<c>openclaw-&lt;kind&gt;</c>), the correct
/// <see cref="OpenClawInvocationKind"/> discriminator, and the raw
/// <c>Squid.Action.OpenClaw.*</c> properties copied into
/// <see cref="OpenClawInvokeIntent.Parameters"/>. Missing / empty properties are omitted so
/// renderers can distinguish "not set" from "explicitly empty". The legacy
/// <c>PrepareAsync</c> path is preserved until Phase 9h flips the pipeline across.
/// </summary>
// xUnit1026: theories share a single <see cref="HandlerCases"/> tuple source; individual
// tests intentionally only read the fields they care about, so unused parameters are
// expected and deliberate.
#pragma warning disable xUnit1026
public class OpenClawActionHandlerDescribeIntentTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ActionExecutionContext MakeContext(string actionType, Dictionary<string, string> properties = null, string stepName = "OpenClaw Step", string actionName = "OpenClaw Action")
    {
        var propList = (properties ?? new Dictionary<string, string>())
            .Select(kv => new DeploymentActionPropertyDto
            {
                PropertyName = kv.Key,
                PropertyValue = kv.Value
            })
            .ToList();

        return new ActionExecutionContext
        {
            Step = new DeploymentStepDto { Name = stepName },
            Action = new DeploymentActionDto
            {
                Name = actionName,
                ActionType = actionType,
                Properties = propList
            },
            Variables = new(),
            ReleaseVersion = "1.0.0"
        };
    }

    public static IEnumerable<object[]> HandlerCases => new[]
    {
        new object[] { typeof(OpenClawWakeActionHandler),           SpecialVariables.ActionTypes.OpenClawWake,           "openclaw-wake",            OpenClawInvocationKind.Wake },
        new object[] { typeof(OpenClawAssertActionHandler),         SpecialVariables.ActionTypes.OpenClawAssert,         "openclaw-assert",          OpenClawInvocationKind.Assert },
        new object[] { typeof(OpenClawChatCompletionActionHandler), SpecialVariables.ActionTypes.OpenClawChatCompletion, "openclaw-chat-completion", OpenClawInvocationKind.ChatCompletion },
        new object[] { typeof(OpenClawFetchResultActionHandler),    SpecialVariables.ActionTypes.OpenClawFetchResult,    "openclaw-fetch-result",    OpenClawInvocationKind.FetchResult },
        new object[] { typeof(OpenClawInvokeToolActionHandler),     SpecialVariables.ActionTypes.OpenClawInvokeTool,     "openclaw-invoke-tool",     OpenClawInvocationKind.InvokeTool },
        new object[] { typeof(OpenClawRunAgentActionHandler),       SpecialVariables.ActionTypes.OpenClawRunAgent,       "openclaw-run-agent",       OpenClawInvocationKind.RunAgent },
        new object[] { typeof(OpenClawWaitSessionActionHandler),    SpecialVariables.ActionTypes.OpenClawWaitSession,    "openclaw-wait-session",    OpenClawInvocationKind.WaitSession }
    };

    private static IActionHandler Instantiate(Type handlerType)
        => (IActionHandler)Activator.CreateInstance(handlerType)!;

    // ------------------------------------------------------------------
    // Shape / name / kind (cross-cutting)
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(HandlerCases))]
    public async Task DescribeIntentAsync_ReturnsOpenClawInvokeIntent(Type handlerType, string actionType, string expectedName, OpenClawInvocationKind expectedKind)
    {
        var handler = Instantiate(handlerType);
        var ctx = MakeContext(actionType);

        var intent = await handler.DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ShouldBeOfType<OpenClawInvokeIntent>();
    }

    [Theory]
    [MemberData(nameof(HandlerCases))]
    public async Task DescribeIntentAsync_HasStableSemanticName(Type handlerType, string actionType, string expectedName, OpenClawInvocationKind expectedKind)
    {
        var handler = Instantiate(handlerType);
        var ctx = MakeContext(actionType);

        var intent = await handler.DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldBe(expectedName);
    }

    [Theory]
    [MemberData(nameof(HandlerCases))]
    public async Task DescribeIntentAsync_NameIsNotLegacyPrefixed(Type handlerType, string actionType, string expectedName, OpenClawInvocationKind expectedKind)
    {
        var handler = Instantiate(handlerType);
        var ctx = MakeContext(actionType);

        var intent = await handler.DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldNotStartWith("legacy:");
    }

    [Theory]
    [MemberData(nameof(HandlerCases))]
    public async Task DescribeIntentAsync_KindMatchesHandler(Type handlerType, string actionType, string expectedName, OpenClawInvocationKind expectedKind)
    {
        var handler = Instantiate(handlerType);
        var ctx = MakeContext(actionType);

        var intent = (OpenClawInvokeIntent)await handler.DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Kind.ShouldBe(expectedKind);
    }

    [Theory]
    [MemberData(nameof(HandlerCases))]
    public async Task DescribeIntentAsync_PropagatesStepAndActionName(Type handlerType, string actionType, string expectedName, OpenClawInvocationKind expectedKind)
    {
        var handler = Instantiate(handlerType);
        var ctx = MakeContext(actionType, stepName: "Wake the robot", actionName: "SendPrompt");

        var intent = await handler.DescribeIntentAsync(ctx, CancellationToken.None);

        intent.StepName.ShouldBe("Wake the robot");
        intent.ActionName.ShouldBe("SendPrompt");
    }

    // ------------------------------------------------------------------
    // Parameters — defaults and filtering
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(HandlerCases))]
    public async Task DescribeIntentAsync_ParametersAreEmpty_WhenActionHasNoProperties(Type handlerType, string actionType, string expectedName, OpenClawInvocationKind expectedKind)
    {
        var handler = Instantiate(handlerType);
        var ctx = MakeContext(actionType);

        var intent = (OpenClawInvokeIntent)await handler.DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Parameters.ShouldNotBeNull();
        intent.Parameters.Count.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(HandlerCases))]
    public async Task DescribeIntentAsync_ParametersSkipEmptyValues(Type handlerType, string actionType, string expectedName, OpenClawInvocationKind expectedKind)
    {
        var handler = Instantiate(handlerType);
        var ctx = MakeContext(actionType, new Dictionary<string, string>
        {
            [SpecialVariables.OpenClaw.PropWakeText] = "",
            [SpecialVariables.OpenClaw.PropPrompt] = "   "
        });

        var intent = (OpenClawInvokeIntent)await handler.DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Parameters.ContainsKey(SpecialVariables.OpenClaw.PropWakeText).ShouldBeFalse();
        intent.Parameters.ContainsKey(SpecialVariables.OpenClaw.PropPrompt).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(HandlerCases))]
    public async Task DescribeIntentAsync_ParametersIgnoreNonOpenClawKeys(Type handlerType, string actionType, string expectedName, OpenClawInvocationKind expectedKind)
    {
        var handler = Instantiate(handlerType);
        var ctx = MakeContext(actionType, new Dictionary<string, string>
        {
            ["Squid.Action.Script.Body"] = "echo unrelated",
            ["RandomKey"] = "value"
        });

        var intent = (OpenClawInvokeIntent)await handler.DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Parameters.ContainsKey("Squid.Action.Script.Body").ShouldBeFalse();
        intent.Parameters.ContainsKey("RandomKey").ShouldBeFalse();
    }

    // ------------------------------------------------------------------
    // Parameters — kind-specific passthrough
    // ------------------------------------------------------------------

    [Fact]
    public async Task Wake_CopiesWakeTextIntoParameters()
    {
        var handler = new OpenClawWakeActionHandler();
        var ctx = MakeContext(SpecialVariables.ActionTypes.OpenClawWake, new Dictionary<string, string>
        {
            [SpecialVariables.OpenClaw.PropWakeText] = "System event occurred",
            [SpecialVariables.OpenClaw.PropWakeMode] = "interactive"
        });

        var intent = (OpenClawInvokeIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Parameters[SpecialVariables.OpenClaw.PropWakeText].ShouldBe("System event occurred");
        intent.Parameters[SpecialVariables.OpenClaw.PropWakeMode].ShouldBe("interactive");
    }

    [Fact]
    public async Task Assert_CopiesAllAssertRelatedProperties()
    {
        var handler = new OpenClawAssertActionHandler();
        var ctx = MakeContext(SpecialVariables.ActionTypes.OpenClawAssert, new Dictionary<string, string>
        {
            [SpecialVariables.OpenClaw.PropJsonPath] = "$.status",
            [SpecialVariables.OpenClaw.PropOperator] = "eq",
            [SpecialVariables.OpenClaw.PropExpected] = "ready",
            [SpecialVariables.OpenClaw.PropSourceVariable] = "LastResult"
        });

        var intent = (OpenClawInvokeIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Parameters[SpecialVariables.OpenClaw.PropJsonPath].ShouldBe("$.status");
        intent.Parameters[SpecialVariables.OpenClaw.PropOperator].ShouldBe("eq");
        intent.Parameters[SpecialVariables.OpenClaw.PropExpected].ShouldBe("ready");
        intent.Parameters[SpecialVariables.OpenClaw.PropSourceVariable].ShouldBe("LastResult");
    }

    [Fact]
    public async Task ChatCompletion_CopiesPromptAndModel()
    {
        var handler = new OpenClawChatCompletionActionHandler();
        var ctx = MakeContext(SpecialVariables.ActionTypes.OpenClawChatCompletion, new Dictionary<string, string>
        {
            [SpecialVariables.OpenClaw.PropPrompt] = "Summarize the release",
            [SpecialVariables.OpenClaw.PropSystemPrompt] = "You are a release note writer.",
            [SpecialVariables.OpenClaw.PropModel] = "claude-opus-4-6",
            [SpecialVariables.OpenClaw.PropThinking] = "false"
        });

        var intent = (OpenClawInvokeIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Parameters[SpecialVariables.OpenClaw.PropPrompt].ShouldBe("Summarize the release");
        intent.Parameters[SpecialVariables.OpenClaw.PropSystemPrompt].ShouldBe("You are a release note writer.");
        intent.Parameters[SpecialVariables.OpenClaw.PropModel].ShouldBe("claude-opus-4-6");
        intent.Parameters[SpecialVariables.OpenClaw.PropThinking].ShouldBe("false");
    }

    [Fact]
    public async Task FetchResult_ParametersRoundTripSourceVariable()
    {
        var handler = new OpenClawFetchResultActionHandler();
        var ctx = MakeContext(SpecialVariables.ActionTypes.OpenClawFetchResult, new Dictionary<string, string>
        {
            [SpecialVariables.OpenClaw.PropSourceVariable] = "WakeResult",
            [SpecialVariables.OpenClaw.PropFieldMappings] = "status=Status;summary=Summary"
        });

        var intent = (OpenClawInvokeIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Parameters[SpecialVariables.OpenClaw.PropSourceVariable].ShouldBe("WakeResult");
        intent.Parameters[SpecialVariables.OpenClaw.PropFieldMappings].ShouldBe("status=Status;summary=Summary");
    }

    [Fact]
    public async Task InvokeTool_CopiesToolAndArgs()
    {
        var handler = new OpenClawInvokeToolActionHandler();
        var ctx = MakeContext(SpecialVariables.ActionTypes.OpenClawInvokeTool, new Dictionary<string, string>
        {
            [SpecialVariables.OpenClaw.PropTool] = "sessions_list",
            [SpecialVariables.OpenClaw.PropToolAction] = "json",
            [SpecialVariables.OpenClaw.PropArgsJson] = "{\"limit\":10}",
            [SpecialVariables.OpenClaw.PropTimeoutSeconds] = "30"
        });

        var intent = (OpenClawInvokeIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Parameters[SpecialVariables.OpenClaw.PropTool].ShouldBe("sessions_list");
        intent.Parameters[SpecialVariables.OpenClaw.PropToolAction].ShouldBe("json");
        intent.Parameters[SpecialVariables.OpenClaw.PropArgsJson].ShouldBe("{\"limit\":10}");
        intent.Parameters[SpecialVariables.OpenClaw.PropTimeoutSeconds].ShouldBe("30");
    }

    [Fact]
    public async Task RunAgent_CopiesMessageAndAgentDetails()
    {
        var handler = new OpenClawRunAgentActionHandler();
        var ctx = MakeContext(SpecialVariables.ActionTypes.OpenClawRunAgent, new Dictionary<string, string>
        {
            [SpecialVariables.OpenClaw.PropMessage] = "Run nightly build",
            [SpecialVariables.OpenClaw.PropAgentId] = "agent-42",
            [SpecialVariables.OpenClaw.PropAgentName] = "NightlyBuilder",
            [SpecialVariables.OpenClaw.PropAgentTimeoutSeconds] = "120"
        });

        var intent = (OpenClawInvokeIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Parameters[SpecialVariables.OpenClaw.PropMessage].ShouldBe("Run nightly build");
        intent.Parameters[SpecialVariables.OpenClaw.PropAgentId].ShouldBe("agent-42");
        intent.Parameters[SpecialVariables.OpenClaw.PropAgentName].ShouldBe("NightlyBuilder");
        intent.Parameters[SpecialVariables.OpenClaw.PropAgentTimeoutSeconds].ShouldBe("120");
    }

    [Fact]
    public async Task WaitSession_CopiesSessionKeyAndPollingProperties()
    {
        var handler = new OpenClawWaitSessionActionHandler();
        var ctx = MakeContext(SpecialVariables.ActionTypes.OpenClawWaitSession, new Dictionary<string, string>
        {
            [SpecialVariables.OpenClaw.PropSessionKey] = "hook:test",
            [SpecialVariables.OpenClaw.PropSuccessPattern] = "DONE",
            [SpecialVariables.OpenClaw.PropFailPattern] = "ERROR",
            [SpecialVariables.OpenClaw.PropMaxWaitSeconds] = "300",
            [SpecialVariables.OpenClaw.PropPollSeconds] = "5"
        });

        var intent = (OpenClawInvokeIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Parameters[SpecialVariables.OpenClaw.PropSessionKey].ShouldBe("hook:test");
        intent.Parameters[SpecialVariables.OpenClaw.PropSuccessPattern].ShouldBe("DONE");
        intent.Parameters[SpecialVariables.OpenClaw.PropFailPattern].ShouldBe("ERROR");
        intent.Parameters[SpecialVariables.OpenClaw.PropMaxWaitSeconds].ShouldBe("300");
        intent.Parameters[SpecialVariables.OpenClaw.PropPollSeconds].ShouldBe("5");
    }

    // ------------------------------------------------------------------
    // Null guards
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(HandlerCases))]
    public async Task DescribeIntentAsync_NullContext_Throws(Type handlerType, string actionType, string expectedName, OpenClawInvocationKind expectedKind)
    {
        var handler = Instantiate(handlerType);

        await Should.ThrowAsync<ArgumentNullException>(
            async () => await handler.DescribeIntentAsync(null!, CancellationToken.None));
    }
}
#pragma warning restore xUnit1026
