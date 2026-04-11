using Squid.Core.Extensions;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Intents;
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

    /// <summary>
    /// Phase 9c.3 — direct intent emission. Bypasses the default <c>PrepareAsync</c> +
    /// <c>LegacyIntentAdapter</c> seam and produces a <see cref="KubernetesKustomizeIntent"/>
    /// with a stable semantic name (<c>k8s-kustomize-apply</c>). The adapter path would
    /// have collapsed this onto a <see cref="KubernetesApplyIntent"/> with an empty
    /// <c>YamlFiles</c> list — which is ambiguous because an empty-yaml k8s-apply is
    /// indistinguishable from a nothing-to-apply no-op. A kustomize overlay is a distinct
    /// semantic: the renderer runs <c>kustomize build</c> on the target filesystem and
    /// only THEN has manifests to apply.
    /// </summary>
    Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var action = ctx.Action;

        var overlayPath = action?.GetProperty(KubernetesKustomizeProperties.OverlayPath) ?? ".";
        var customKustomizePath = action?.GetProperty(KubernetesKustomizeProperties.CustomKustomizePath) ?? string.Empty;
        var additionalArgs = action?.GetProperty(KubernetesKustomizeProperties.AdditionalArgs) ?? string.Empty;

        var namespace_ = KubernetesYamlActionHandler.GetNamespaceFromAction(action);

        var serverSideApply = action?.GetProperty(KubernetesProperties.ServerSideApplyEnabled) == KubernetesBooleanValues.True;
        var fieldManager = action?.GetProperty(KubernetesProperties.ServerSideApplyFieldManager) ?? "squid-deploy";
        var forceConflicts = action?.GetProperty(KubernetesProperties.ServerSideApplyForceConflicts) == KubernetesBooleanValues.True;

        var intent = new KubernetesKustomizeIntent
        {
            Name = "k8s-kustomize-apply",
            StepName = ctx.Step?.Name ?? string.Empty,
            ActionName = action?.Name ?? string.Empty,
            OverlayPath = overlayPath,
            CustomKustomizePath = customKustomizePath,
            AdditionalArgs = additionalArgs,
            Namespace = namespace_,
            ServerSideApply = serverSideApply,
            FieldManager = fieldManager,
            ForceConflicts = forceConflicts
        };

        return Task.FromResult<ExecutionIntent>(intent);
    }
}
