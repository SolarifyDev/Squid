using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Handlers;

public class RunScriptActionHandlerTests
{
    private readonly RunScriptActionHandler _handler = new();

    private static DeploymentActionDto CreateAction(
        string actionType = "Squid.Script",
        string scriptBody = null,
        string syntax = null)
    {
        var action = new DeploymentActionDto
        {
            ActionType = actionType,
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (scriptBody != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.Script.ScriptBody",
                PropertyValue = scriptBody
            });
        }

        if (syntax != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.Script.Syntax",
                PropertyValue = syntax
            });
        }

        return action;
    }

    private static ActionExecutionContext CreateContext(DeploymentActionDto action) => new()
    {
        Action = action
    };

    // === CanHandle Tests (default interface implementation) ===

    [Fact]
    public void CanHandle_MatchingActionType_ReturnsTrue()
    {
        var action = CreateAction("Squid.Script");
        ((IActionHandler)_handler).CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_CaseInsensitive_ReturnsTrue()
    {
        var action = CreateAction("squid.script");
        ((IActionHandler)_handler).CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_DifferentActionType_ReturnsFalse()
    {
        var action = CreateAction("Squid.HelmChartUpgrade");
        ((IActionHandler)_handler).CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullAction_ReturnsFalse()
    {
        ((IActionHandler)_handler).CanHandle(null).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullActionType_ReturnsFalse()
    {
        var action = new DeploymentActionDto { ActionType = null };
        ((IActionHandler)_handler).CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void ActionType_ReturnsExpectedValue()
    {
        _handler.ActionType.ShouldBe(SpecialVariables.ActionTypes.Script);
    }

    // === PrepareAsync Tests ===

    [Fact]
    public async Task PrepareAsync_WithScriptBody_ReturnsScriptInResult()
    {
        var action = CreateAction(scriptBody: "kubectl get pods -n production");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldNotBeNull();
        result.ScriptBody.ShouldBe("kubectl get pods -n production");
    }

    [Theory]
    [InlineData("Bash", ScriptSyntax.Bash)]
    [InlineData("PowerShell", ScriptSyntax.PowerShell)]
    [InlineData("CSharp", ScriptSyntax.CSharp)]
    [InlineData("FSharp", ScriptSyntax.FSharp)]
    [InlineData("Python", ScriptSyntax.Python)]
    public async Task PrepareAsync_Syntax_ResolvedCorrectly(string input, ScriptSyntax expected)
    {
        var action = CreateAction(scriptBody: "echo hi", syntax: input);
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(expected);
    }

    [Fact]
    public async Task PrepareAsync_NoSyntaxSpecified_DefaultsToPowerShell()
    {
        var action = CreateAction(scriptBody: "echo hi");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    [Fact]
    public async Task PrepareAsync_UnknownSyntax_DefaultsToPowerShell()
    {
        var action = CreateAction(scriptBody: "echo hi", syntax: "Ruby");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    [Fact]
    public async Task PrepareAsync_NoScriptBody_ReturnsEmptyString()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task PrepareAsync_CalamariCommand_IsNull()
    {
        var action = CreateAction(scriptBody: "echo hi");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.CalamariCommand.ShouldBeNull();
        result.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Apply);
        result.PayloadKind.ShouldBe(PayloadKind.None);
    }

    [Fact]
    public async Task PrepareAsync_MultiLineScript_PreservesContent()
    {
        var script = "kubectl get pods\nkubectl get services\nkubectl get ingress";
        var action = CreateAction(scriptBody: script);
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldBe(script);
    }

    [Fact]
    public async Task PrepareAsync_BashSyntaxCaseInsensitive_SetsBash()
    {
        var action = CreateAction(scriptBody: "echo hi", syntax: "bash");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public async Task PrepareAsync_FilesAlwaysEmpty()
    {
        var action = CreateAction(scriptBody: "kubectl get pods");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_NullProperties_ReturnsEmptyScriptBody()
    {
        var action = new DeploymentActionDto
        {
            ActionType = "Squid.Script",
            Properties = null
        };
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldBe(string.Empty);
    }
}
