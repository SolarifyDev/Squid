using Squid.Core.Extensions;
using Squid.Core.Services.Common;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesKustomizeActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.KubernetesKustomize;

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var syntaxStr = ctx.Action.GetProperty(SpecialVariables.Action.ScriptSyntax);
        var syntax = string.Equals(syntaxStr, ScriptSyntax.Bash.ToString(), StringComparison.OrdinalIgnoreCase)
            ? ScriptSyntax.Bash
            : ScriptSyntax.PowerShell;

        var templateName = syntax == ScriptSyntax.Bash ? "KubernetesKustomize.sh" : "KubernetesKustomize.ps1";
        var template = UtilService.GetEmbeddedScriptContent(templateName);

        var overlayPath = ctx.Action.GetProperty(KubernetesKustomizeProperties.OverlayPath) ?? ".";
        var customKustomizePath = ctx.Action.GetProperty(KubernetesKustomizeProperties.CustomKustomizePath) ?? string.Empty;
        var additionalArgs = ctx.Action.GetProperty(KubernetesKustomizeProperties.AdditionalArgs) ?? string.Empty;
        var applyFlags = BuildApplyFlags(ctx.Action);

        var scriptBody = template
            .Replace("{{OverlayPath}}", overlayPath, StringComparison.Ordinal)
            .Replace("{{KustomizeExe}}", customKustomizePath, StringComparison.Ordinal)
            .Replace("{{AdditionalArgs}}", additionalArgs, StringComparison.Ordinal)
            .Replace("{{ApplyFlags}}", applyFlags, StringComparison.Ordinal);

        var result = new ActionExecutionResult
        {
            ScriptBody = scriptBody,
            Files = new Dictionary<string, byte[]>(),
            CalamariCommand = null,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Syntax = syntax
        };

        return Task.FromResult(result);
    }

    private static string BuildApplyFlags(DeploymentActionDto action)
    {
        var flags = string.Empty;

        if (action.GetProperty(KubernetesProperties.ServerSideApplyEnabled) == KubernetesBooleanValues.True)
        {
            var fieldManager = action.GetProperty(KubernetesProperties.ServerSideApplyFieldManager) ?? "squid-deploy";
            flags += $"--server-side --field-manager={fieldManager}";

            if (action.GetProperty(KubernetesProperties.ServerSideApplyForceConflicts) == KubernetesBooleanValues.True)
                flags += " --force-conflicts";
        }

        return flags;
    }
}
