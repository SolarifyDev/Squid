using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesKustomizeActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.KubernetesKustomize;

    /// <summary>
    /// Direct intent emission. Produces a <see cref="KubernetesKustomizeIntent"/> with a
    /// stable semantic name (<c>k8s-kustomize-apply</c>). A kustomize overlay is a distinct
    /// semantic: the renderer runs <c>kustomize build</c> on the target filesystem and only
    /// THEN has manifests to apply.
    /// </summary>
    Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var action = ctx.Action;
        var syntax = ScriptSyntaxHelper.ResolveSyntax(action);

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
            Syntax = syntax,
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
