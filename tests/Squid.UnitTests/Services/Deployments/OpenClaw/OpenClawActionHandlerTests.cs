using Squid.Core.Services.DeploymentExecution.OpenClaw;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Deployments.OpenClaw;

public class OpenClawActionHandlerTests
{
    [Fact]
    public void InvokeTool_ActionType_MatchesConstant()
    {
        new OpenClawInvokeToolActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawInvokeTool);
    }

    [Fact]
    public void RunAgent_ActionType_MatchesConstant()
    {
        new OpenClawRunAgentActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawRunAgent);
    }

    [Fact]
    public void Wake_ActionType_MatchesConstant()
    {
        new OpenClawWakeActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawWake);
    }

    [Fact]
    public void WaitSession_ActionType_MatchesConstant()
    {
        new OpenClawWaitSessionActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawWaitSession);
    }

    [Fact]
    public void Assert_ActionType_MatchesConstant()
    {
        new OpenClawAssertActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawAssert);
    }

    [Fact]
    public void FetchResult_ActionType_MatchesConstant()
    {
        new OpenClawFetchResultActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawFetchResult);
    }

    [Fact]
    public void ChatCompletion_ActionType_MatchesConstant()
    {
        new OpenClawChatCompletionActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawChatCompletion);
    }
}
