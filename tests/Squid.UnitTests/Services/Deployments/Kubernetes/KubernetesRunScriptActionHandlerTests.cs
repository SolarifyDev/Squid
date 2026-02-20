using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesRunScriptActionHandlerTests
{
    private readonly KubernetesRunScriptActionHandler _handler = new();

    private static DeploymentActionDto CreateAction(
        string actionType = "Squid.KubernetesRunScript",
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

    // === CanHandle Tests ===

    [Fact]
    public void CanHandle_MatchingActionType_ReturnsTrue()
    {
        var action = CreateAction("Squid.KubernetesRunScript");
        _handler.CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_CaseInsensitive_ReturnsTrue()
    {
        var action = CreateAction("squid.kubernetesrunscript");
        _handler.CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_DifferentActionType_ReturnsFalse()
    {
        var action = CreateAction("Squid.HelmChartUpgrade");
        _handler.CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullAction_ReturnsFalse()
    {
        _handler.CanHandle(null).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullActionType_ReturnsFalse()
    {
        var action = new DeploymentActionDto { ActionType = null };
        _handler.CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void ActionType_ReturnsExpectedValue()
    {
        _handler.ActionType.ShouldBe("Squid.KubernetesRunScript");
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

    [Fact]
    public async Task PrepareAsync_BashSyntax_SetsSyntaxToBash()
    {
        var action = CreateAction(scriptBody: "echo hi", syntax: "Bash");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public async Task PrepareAsync_PowerShellSyntax_SetsSyntaxToPowerShell()
    {
        var action = CreateAction(scriptBody: "Write-Host hi", syntax: "PowerShell");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(ScriptSyntax.PowerShell);
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
    public async Task PrepareAsync_UnknownSyntax_DefaultsToPowerShell()
    {
        var action = CreateAction(scriptBody: "echo hi", syntax: "Python");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(ScriptSyntax.PowerShell);
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
            ActionType = "Squid.KubernetesRunScript",
            Properties = null
        };
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldBe(string.Empty);
    }
}
