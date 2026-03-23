using System.Collections.Generic;
using System.Text;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesDeployYamlActionHandlerTests
{
    private readonly KubernetesDeployYamlActionHandler _handler = new();

    private static DeploymentActionDto CreateAction(
        string actionType = "Squid.KubernetesDeployRawYaml",
        string inlineYaml = null,
        string syntax = null)
    {
        var action = new DeploymentActionDto
        {
            ActionType = actionType,
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (inlineYaml != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesYaml.InlineYaml",
                PropertyValue = inlineYaml
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
        var action = CreateAction("Squid.KubernetesDeployRawYaml");
        ((IActionHandler)_handler).CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_CaseInsensitive_ReturnsTrue()
    {
        var action = CreateAction("squid.kubernetesdeployrawyaml");
        ((IActionHandler)_handler).CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_DifferentActionType_ReturnsFalse()
    {
        var action = CreateAction("Squid.KubernetesRunScript");
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
        _handler.ActionType.ShouldBe(SpecialVariables.ActionTypes.KubernetesDeployRawYaml);
    }

    // === PrepareAsync — Inline YAML Tests ===

    [Fact]
    public async Task PrepareAsync_InlineYaml_CreatesYamlFile()
    {
        var yaml = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: test";
        var action = CreateAction(inlineYaml: yaml);
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldContainKey("inline-deployment.yaml");
        var fileContent = Encoding.UTF8.GetString(result.Files["inline-deployment.yaml"]);
        fileContent.ShouldBe(yaml);
    }

    [Fact]
    public async Task PrepareAsync_InlineYaml_Bash_GeneratesApplyCommand()
    {
        var yaml = "apiVersion: v1\nkind: Pod";
        var action = CreateAction(inlineYaml: yaml, syntax: "Bash");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("kubectl apply -f");
        result.ScriptBody.ShouldContain("./inline-deployment.yaml");
    }

    [Fact]
    public async Task PrepareAsync_InlineYaml_PowerShell_GeneratesApplyCommand()
    {
        var yaml = "apiVersion: v1\nkind: Pod";
        var action = CreateAction(inlineYaml: yaml, syntax: "PowerShell");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("kubectl apply -f");
        result.ScriptBody.ShouldContain(".\\inline-deployment.yaml");
    }

    // === PrepareAsync — No Inline YAML (Content Dir) Tests ===

    [Fact]
    public async Task PrepareAsync_NoInlineYaml_Bash_AppliesContentDir()
    {
        var action = CreateAction(syntax: "Bash");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("kubectl apply -f \"./content/\"");
        result.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_NoInlineYaml_PowerShell_AppliesContentDir()
    {
        var action = CreateAction(syntax: "PowerShell");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("kubectl apply -f \".\\content\\\"");
        result.Files.ShouldBeEmpty();
    }

    // === General Result Tests ===

    [Fact]
    public async Task PrepareAsync_CalamariCommand_IsNull()
    {
        var action = CreateAction(inlineYaml: "apiVersion: v1");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.CalamariCommand.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_DefaultSyntax_IsPowerShell()
    {
        var action = CreateAction(inlineYaml: "apiVersion: v1");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    [Fact]
    public async Task PrepareAsync_EmptyInlineYaml_TreatedAsNoYaml()
    {
        var action = CreateAction(inlineYaml: "   ");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        // Whitespace-only is treated as no inline YAML — falls back to content dir
        result.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_MultiDocumentYaml_PreservesAll()
    {
        var yaml = "apiVersion: v1\nkind: Service\n---\napiVersion: v1\nkind: Deployment";
        var action = CreateAction(inlineYaml: yaml);
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        var fileContent = Encoding.UTF8.GetString(result.Files["inline-deployment.yaml"]);
        fileContent.ShouldContain("---");
        fileContent.ShouldContain("Service");
        fileContent.ShouldContain("Deployment");
    }

    [Fact]
    public async Task PrepareAsync_NullProperties_FallsBackToContentDir()
    {
        var action = new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployRawYaml",
            Properties = null
        };
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldBeEmpty();
        result.ScriptBody.ShouldContain("kubectl apply -f");
    }

    [Fact]
    public async Task PrepareAsync_BashSyntax_SetsSyntaxToBash()
    {
        var action = CreateAction(inlineYaml: "apiVersion: v1", syntax: "Bash");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    // === Server-Side Apply Tests ===

    [Fact]
    public async Task PrepareAsync_ServerSideApplyEnabled_GeneratedScriptContainsServerSideFlag()
    {
        var action = CreateAction(inlineYaml: "apiVersion: v1\nkind: Pod", syntax: "Bash");
        action.Properties.Add(new DeploymentActionPropertyDto
        {
            PropertyName = "Squid.Action.Kubernetes.ServerSideApply.Enabled",
            PropertyValue = "True"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("--server-side");
        result.ScriptBody.ShouldContain("--field-manager=\"squid-deploy\"");
    }

    [Fact]
    public async Task PrepareAsync_ServerSideApplyDisabled_NoServerSideFlag()
    {
        var action = CreateAction(inlineYaml: "apiVersion: v1\nkind: Pod", syntax: "Bash");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldNotContain("--server-side");
    }
}
