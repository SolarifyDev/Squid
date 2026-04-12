using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.VariableSubstitution;

namespace Squid.Core.Services.DeploymentExecution.Variables;

/// <summary>
/// Expands <c>#{variable}</c> references in <see cref="ExecutionIntent"/> string fields.
/// Called after <c>DescribeIntentAsync</c> to apply the second-pass variable substitution
/// that was previously handled by <c>VariableExpander.ExpandString(ScriptBody)</c> in the
/// legacy <c>PrepareAsync</c> pipeline.
///
/// <para>Action properties are already expanded before <c>DescribeIntentAsync</c> runs, so most
/// intent fields contain concrete values. This pass catches any remaining
/// <c>#{variable}</c> references — especially in <see cref="RunScriptIntent.ScriptBody"/>,
/// which may contain user-authored variable tokens.</para>
/// </summary>
public static class IntentVariableExpander
{
    public static ExecutionIntent Expand(ExecutionIntent intent, VariableDictionary variableDictionary)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(variableDictionary);

        return intent switch
        {
            RunScriptIntent rs => ExpandRunScript(rs, variableDictionary),
            HelmUpgradeIntent h => ExpandHelmUpgrade(h, variableDictionary),
            KubernetesApplyIntent a => ExpandKubernetesApply(a, variableDictionary),
            KubernetesKustomizeIntent k => ExpandKustomize(k, variableDictionary),
            OpenClawInvokeIntent oc => ExpandOpenClaw(oc, variableDictionary),
            ManualInterventionIntent mi => ExpandManualIntervention(mi, variableDictionary),
            _ => intent
        };
    }

    private static RunScriptIntent ExpandRunScript(RunScriptIntent intent, VariableDictionary dict)
    {
        return intent with
        {
            ScriptBody = ExpandString(intent.ScriptBody, dict)
        };
    }

    private static HelmUpgradeIntent ExpandHelmUpgrade(HelmUpgradeIntent intent, VariableDictionary dict)
    {
        return intent with
        {
            ReleaseName = ExpandString(intent.ReleaseName, dict) ?? intent.ReleaseName,
            ChartReference = ExpandString(intent.ChartReference, dict) ?? intent.ChartReference,
            Namespace = ExpandString(intent.Namespace, dict) ?? intent.Namespace,
            CustomHelmExecutable = ExpandString(intent.CustomHelmExecutable, dict) ?? intent.CustomHelmExecutable,
            AdditionalArgs = ExpandString(intent.AdditionalArgs, dict) ?? intent.AdditionalArgs,
            Timeout = ExpandString(intent.Timeout, dict) ?? intent.Timeout,
            InlineValues = ExpandDictionary(intent.InlineValues, dict)
        };
    }

    private static KubernetesApplyIntent ExpandKubernetesApply(KubernetesApplyIntent intent, VariableDictionary dict)
    {
        return intent with
        {
            Namespace = ExpandString(intent.Namespace, dict) ?? intent.Namespace
        };
    }

    private static KubernetesKustomizeIntent ExpandKustomize(KubernetesKustomizeIntent intent, VariableDictionary dict)
    {
        return intent with
        {
            OverlayPath = ExpandString(intent.OverlayPath, dict) ?? intent.OverlayPath,
            CustomKustomizePath = ExpandString(intent.CustomKustomizePath, dict) ?? intent.CustomKustomizePath,
            Namespace = ExpandString(intent.Namespace, dict) ?? intent.Namespace,
            AdditionalArgs = ExpandString(intent.AdditionalArgs, dict) ?? intent.AdditionalArgs
        };
    }

    private static OpenClawInvokeIntent ExpandOpenClaw(OpenClawInvokeIntent intent, VariableDictionary dict)
    {
        return intent with
        {
            Parameters = ExpandDictionary(intent.Parameters, dict)
        };
    }

    private static ManualInterventionIntent ExpandManualIntervention(ManualInterventionIntent intent, VariableDictionary dict)
    {
        return intent with
        {
            Instructions = ExpandString(intent.Instructions, dict) ?? intent.Instructions
        };
    }

    private static string? ExpandString(string? input, VariableDictionary dict)
    {
        return VariableExpander.ExpandString(input, dict);
    }

    private static IReadOnlyDictionary<string, string> ExpandDictionary(IReadOnlyDictionary<string, string> source, VariableDictionary dict)
    {
        if (source.Count == 0)
            return source;

        var expanded = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);

        foreach (var kv in source)
            expanded[kv.Key] = ExpandString(kv.Value, dict) ?? kv.Value;

        return expanded;
    }
}
