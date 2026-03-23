using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesKustomizeActionHandlerTests
{
    private readonly KubernetesKustomizeActionHandler _handler = new();

    private static DeploymentActionDto CreateAction(Dictionary<string, string> properties = null)
    {
        var action = new DeploymentActionDto
        {
            Id = 1,
            Name = "kustomize-deploy",
            ActionType = SpecialVariables.ActionTypes.KubernetesKustomize,
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (properties != null)
        {
            foreach (var kvp in properties)
                action.Properties.Add(new DeploymentActionPropertyDto { ActionId = 1, PropertyName = kvp.Key, PropertyValue = kvp.Value });
        }

        return action;
    }

    private static ActionExecutionContext CreateContext(DeploymentActionDto action) => new() { Action = action };

    [Fact]
    public void CanHandle_MatchingType_ReturnsTrue()
    {
        var action = CreateAction();
        ((IActionHandler)_handler).CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_NonMatchingType_ReturnsFalse()
    {
        var action = CreateAction();
        action.ActionType = "Squid.KubernetesRunScript";
        ((IActionHandler)_handler).CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public async Task PrepareAsync_DefaultOverlay_ContainsKubectlKustomize()
    {
        var action = CreateAction(new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("kubectl kustomize");
    }

    [Fact]
    public async Task PrepareAsync_CustomOverlayPath_UsesCustomPath()
    {
        var action = CreateAction(new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.KubernetesKustomize.OverlayPath"] = "./overlays/production"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(ShellEscapeHelper.Base64Encode("./overlays/production"));
    }

    [Fact]
    public async Task PrepareAsync_CustomKustomizeExe_UsesCustomPath()
    {
        var action = CreateAction(new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.KubernetesKustomize.CustomKustomizePath"] = "/usr/bin/kustomize build"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(ShellEscapeHelper.Base64Encode("/usr/bin/kustomize build"));
    }

    [Fact]
    public async Task PrepareAsync_AdditionalArgs_AppendedToCommand()
    {
        var action = CreateAction(new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.KubernetesKustomize.AdditionalArgs"] = "--enable-helm"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(ShellEscapeHelper.Base64Encode("--enable-helm"));
    }

    [Fact]
    public async Task PrepareAsync_ServerSideApplyEnabled_ContainsServerSideFlags()
    {
        var action = CreateAction(new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Kubernetes.ServerSideApply.Enabled"] = "True"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(ShellEscapeHelper.Base64Encode("--server-side --field-manager=\"squid-deploy\""));
    }

    [Fact]
    public async Task PrepareAsync_Bash_UsesShellScript()
    {
        var action = CreateAction(new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("#!/usr/bin/env bash");
        result.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public async Task PrepareAsync_PowerShell_UsesPowerShellScript()
    {
        var action = CreateAction(new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "PowerShell"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("$ErrorActionPreference");
        result.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }
}
