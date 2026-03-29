using Squid.Core.Extensions;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
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
        var syntax = ScriptSyntaxHelper.ResolveSyntax(ctx.Action);

        var templateName = syntax == ScriptSyntax.Bash ? "KubernetesKustomize.sh" : "KubernetesKustomize.ps1";
        var template = UtilService.GetEmbeddedScriptContent(templateName);

        var overlayPath = ctx.Action.GetProperty(KubernetesKustomizeProperties.OverlayPath) ?? ".";
        var customKustomizePath = ctx.Action.GetProperty(KubernetesKustomizeProperties.CustomKustomizePath) ?? string.Empty;
        var additionalArgs = ctx.Action.GetProperty(KubernetesKustomizeProperties.AdditionalArgs) ?? string.Empty;
        var applyFlags = BuildApplyFlags(ctx.Action);

        string B64(string value) => ShellEscapeHelper.Base64Encode(value ?? string.Empty);

        var scriptBody = template
            .Replace("{{OverlayPath}}", B64(overlayPath), StringComparison.Ordinal)
            .Replace("{{KustomizeExe}}", B64(customKustomizePath), StringComparison.Ordinal)
            .Replace("{{AdditionalArgs}}", B64(additionalArgs), StringComparison.Ordinal)
            .Replace("{{ApplyFlags}}", B64(applyFlags), StringComparison.Ordinal);

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
            flags += $"--server-side --field-manager=\"{fieldManager}\"";

            if (action.GetProperty(KubernetesProperties.ServerSideApplyForceConflicts) == KubernetesBooleanValues.True)
                flags += " --force-conflicts";
        }

        return flags;
    }
}
