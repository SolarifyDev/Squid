using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Rendering.Adapters;

/// <summary>
/// Converts a legacy <see cref="ActionExecutionResult"/> (produced by today's
/// <c>IActionHandler.PrepareAsync</c>) into the matching <see cref="ExecutionIntent"/>
/// subtype. Phase 5 uses this to feed the new renderer layer with an intent shaped
/// roughly like what Phase 9 handlers will emit directly.
///
/// <para>
/// The adapter is intentionally lossy — it discards transport-specific fields
/// (<c>CalamariCommand</c>, <c>ExecutionMode</c>, wrapping state, etc.) that the
/// Phase-5 renderer recovers from <c>IntentRenderContext.LegacyRequest</c>. Once the
/// handlers are migrated (Phase 9) the adapter is removed.
/// </para>
/// </summary>
public static class LegacyIntentAdapter
{
    /// <summary>
    /// Build an <see cref="ExecutionIntent"/> from a legacy <see cref="ActionExecutionResult"/>.
    /// Matches on <c>ActionType</c> to pick the intent subtype; unknown action types fall
    /// back to <see cref="RunScriptIntent"/> so the renderer pipeline is never blocked.
    /// </summary>
    public static ExecutionIntent FromLegacyResult(ActionExecutionResult result, string stepName)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));

        var actionType = result.ActionType ?? string.Empty;
        var intentName = BuildIntentName(actionType);
        var actionName = result.ActionName ?? string.Empty;
        var assets = BuildAssets(result);

        return actionType switch
        {
            SpecialVariables.ActionTypes.Script
                => BuildRunScriptIntent(result, intentName, stepName, actionName, assets),

            SpecialVariables.ActionTypes.HealthCheck
                => BuildHealthCheckIntent(result, intentName, stepName, actionName, assets),

            SpecialVariables.ActionTypes.HelmChartUpgrade
                => BuildHelmUpgradeIntent(result, intentName, stepName, actionName, assets),

            SpecialVariables.ActionTypes.KubernetesDeployRawYaml
                or SpecialVariables.ActionTypes.KubernetesDeployContainers
                or SpecialVariables.ActionTypes.KubernetesDeployIngress
                or SpecialVariables.ActionTypes.KubernetesDeployService
                or SpecialVariables.ActionTypes.KubernetesDeployConfigMap
                or SpecialVariables.ActionTypes.KubernetesDeploySecret
                or SpecialVariables.ActionTypes.KubernetesKustomize
                => BuildKubernetesApplyIntent(result, intentName, stepName, actionName, assets),

            _ => BuildRunScriptIntent(result, intentName, stepName, actionName, assets)
        };
    }

    private static string BuildIntentName(string actionType)
    {
        if (string.IsNullOrEmpty(actionType)) return "legacy:unknown";

        return $"legacy:{actionType}";
    }

    private static IReadOnlyList<DeploymentFile> BuildAssets(ActionExecutionResult result)
    {
        if (result.Files is null || result.Files.Count == 0)
            return Array.Empty<DeploymentFile>();

        return DeploymentFileCollection.FromLegacyFiles(result.Files).ToList();
    }

    private static RunScriptIntent BuildRunScriptIntent(
        ActionExecutionResult result,
        string intentName,
        string stepName,
        string actionName,
        IReadOnlyList<DeploymentFile> assets)
        => new()
        {
            Name = intentName,
            StepName = stepName,
            ActionName = actionName,
            ScriptBody = result.ScriptBody ?? string.Empty,
            Syntax = result.Syntax,
            Assets = assets,
            InjectRuntimeBundle = false
        };

    private static HealthCheckIntent BuildHealthCheckIntent(
        ActionExecutionResult result,
        string intentName,
        string stepName,
        string actionName,
        IReadOnlyList<DeploymentFile> assets)
        => new()
        {
            Name = intentName,
            StepName = stepName,
            ActionName = actionName,
            Assets = assets,
            CustomScript = string.IsNullOrEmpty(result.ScriptBody) ? null : result.ScriptBody,
            Syntax = result.Syntax
        };

    private static KubernetesApplyIntent BuildKubernetesApplyIntent(
        ActionExecutionResult result,
        string intentName,
        string stepName,
        string actionName,
        IReadOnlyList<DeploymentFile> assets)
        => new()
        {
            Name = intentName,
            StepName = stepName,
            ActionName = actionName,
            YamlFiles = assets,
            Assets = assets,
            Namespace = string.Empty,
            ServerSideApply = false
        };

    private static HelmUpgradeIntent BuildHelmUpgradeIntent(
        ActionExecutionResult result,
        string intentName,
        string stepName,
        string actionName,
        IReadOnlyList<DeploymentFile> assets)
        => new()
        {
            Name = intentName,
            StepName = stepName,
            ActionName = actionName,
            ReleaseName = actionName,
            ChartReference = string.Empty,
            Assets = assets,
            Namespace = string.Empty
        };
}
