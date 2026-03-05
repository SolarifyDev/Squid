using System.Text;
using System.Text.Json;
using Squid.Core.Extensions;
using Squid.Core.Services.Common;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class HelmUpgradeActionHandler : IActionHandler
{
    public DeploymentActionType ActionType => DeploymentActionType.HelmChartUpgrade;

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null) return false;

        return DeploymentActionTypeParser.Is(action.ActionType, ActionType);
    }

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var syntaxStr = ctx.Action.GetProperty(KubernetesScriptProperties.Syntax);
        var syntax = string.Equals(syntaxStr, ScriptSyntax.Bash.ToString(), StringComparison.OrdinalIgnoreCase)
            ? ScriptSyntax.Bash
            : ScriptSyntax.PowerShell;

        var templateName = syntax == ScriptSyntax.Bash ? "HelmUpgrade.sh" : "HelmUpgrade.ps1";
        var template = UtilService.GetEmbeddedScriptContent(templateName);

        var releaseName = ctx.Action.GetProperty(KubernetesHelmProperties.ReleaseName) ?? ctx.Action.Name ?? "release";
        var chartPath = ctx.Action.GetProperty(KubernetesHelmProperties.ChartPath) ?? ".";
        var namespace_ = KubernetesYamlActionHandler.GetNamespaceFromAction(ctx.Action);
        var helmExe = ctx.Action.GetProperty(KubernetesHelmProperties.CustomHelmExecutable) ?? string.Empty;
        var resetValues = ctx.Action.GetProperty(KubernetesHelmProperties.ResetValues) ?? KubernetesBooleanValues.True;
        var helmWait = ctx.Action.GetProperty(KubernetesHelmProperties.ClientVersion) != null ? KubernetesBooleanValues.True : KubernetesBooleanValues.False;
        var additionalArgs = ctx.Action.GetProperty(KubernetesHelmProperties.AdditionalArgs) ?? string.Empty;

        var files = new Dictionary<string, byte[]>();
        var valuesFilesBlock = BuildValuesFilesBlock(ctx.Action, syntax, files);
        var setValuesBlock = BuildSetValuesBlock(ctx.Action, syntax);

        var scriptBody = template
            .Replace("{{ReleaseName}}", releaseName, StringComparison.Ordinal)
            .Replace("{{ChartPath}}", chartPath, StringComparison.Ordinal)
            .Replace("{{Namespace}}", namespace_, StringComparison.Ordinal)
            .Replace("{{HelmExe}}", helmExe, StringComparison.Ordinal)
            .Replace("{{ResetValues}}", resetValues, StringComparison.Ordinal)
            .Replace("{{HelmWait}}", helmWait, StringComparison.Ordinal)
            .Replace("{{AdditionalArgs}}", additionalArgs, StringComparison.Ordinal)
            .Replace("{{ValuesFilesBlock}}", valuesFilesBlock, StringComparison.Ordinal)
            .Replace("{{SetValuesBlock}}", setValuesBlock, StringComparison.Ordinal);

        var result = new ActionExecutionResult
        {
            ScriptBody = scriptBody,
            Files = files,
            CalamariCommand = null,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Syntax = syntax
        };

        return Task.FromResult(result);
    }

    private static string BuildValuesFilesBlock(DeploymentActionDto action, ScriptSyntax syntax, Dictionary<string, byte[]> files)
    {
        var rawYaml = action.GetProperty(KubernetesHelmProperties.YamlValues);

        if (string.IsNullOrWhiteSpace(rawYaml)) return string.Empty;

        const string rawYamlFileName = "rawYamlValues.yaml";
        files[rawYamlFileName] = Encoding.UTF8.GetBytes(rawYaml);

        var sb = new StringBuilder();

        if (syntax == ScriptSyntax.Bash)
            sb.AppendLine($"HELM_CMD+=(\"--values\" \"./{rawYamlFileName}\")");
        else
            sb.AppendLine($"$helmArgs += \"--values\"; $helmArgs += \".\\{rawYamlFileName}\"");

        return sb.ToString();
    }

    private static string BuildSetValuesBlock(DeploymentActionDto action, ScriptSyntax syntax)
    {
        var keyValuesRaw = action.GetProperty(KubernetesHelmProperties.KeyValues);

        if (string.IsNullOrWhiteSpace(keyValuesRaw)) return string.Empty;

        var sb = new StringBuilder();

        try
        {
            var keyValues = JsonSerializer.Deserialize<Dictionary<string, string>>(keyValuesRaw);

            if (keyValues == null) return string.Empty;

            foreach (var kvp in keyValues)
            {
                AppendSetValue(sb, syntax, kvp.Key, kvp.Value);
            }
        }
        catch (JsonException)
        {
            var pairs = keyValuesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var pair in pairs)
            {
                AppendSetValueRaw(sb, syntax, pair);
            }
        }

        return sb.ToString();
    }

    private static void AppendSetValue(StringBuilder sb, ScriptSyntax syntax, string key, string value)
    {
        if (syntax == ScriptSyntax.Bash)
        {
            var escapedValue = value.Replace("'", "'\\''", StringComparison.Ordinal);
            sb.AppendLine($"HELM_CMD+=(\"--set\" \"{key}='{escapedValue}'\")");
        }
        else
        {
            var escapedValue = EscapePowerShellValue(value);
            sb.AppendLine($"$helmArgs += \"--set\"; $helmArgs += \"{key}={escapedValue}\"");
        }
    }

    private static void AppendSetValueRaw(StringBuilder sb, ScriptSyntax syntax, string setValue)
    {
        if (syntax == ScriptSyntax.Bash)
            sb.AppendLine($"HELM_CMD+=(\"--set\" \"{setValue}\")");
        else
        {
            var escapedSetValue = EscapePowerShellValue(setValue);
            sb.AppendLine($"$helmArgs += \"--set\"; $helmArgs += \"{escapedSetValue}\"");
        }
    }

    private static string EscapePowerShellValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        return value
            .Replace("`", "``", StringComparison.Ordinal)
            .Replace("\"", "`\"", StringComparison.Ordinal)
            .Replace("$", "`$", StringComparison.Ordinal);
    }
}
