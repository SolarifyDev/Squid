using System.Text;
using System.Text.Json;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class HelmUpgradeActionHandler : IActionHandler
{
    private const string HelmChartUpgradeActionType = "Squid.HelmChartUpgrade";

    public string ActionType => HelmChartUpgradeActionType;

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null) return false;

        return string.Equals(action.ActionType, HelmChartUpgradeActionType, StringComparison.OrdinalIgnoreCase);
    }

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var syntaxStr = GetPropertyValue(ctx.Action, "Squid.Action.Script.Syntax");
        var syntax = string.Equals(syntaxStr, "Bash", StringComparison.OrdinalIgnoreCase)
            ? ScriptSyntax.Bash
            : ScriptSyntax.PowerShell;

        var templateName = syntax == ScriptSyntax.Bash ? "HelmUpgrade.sh" : "HelmUpgrade.ps1";
        var template = UtilService.GetEmbeddedScriptContent(templateName);

        var releaseName = GetPropertyValue(ctx.Action, "Squid.Action.Helm.ReleaseName") ?? ctx.Action.Name ?? "release";
        var chartPath = GetPropertyValue(ctx.Action, "Squid.Action.Helm.ChartPath") ?? ".";
        var namespace_ = GetPropertyValue(ctx.Action, "Squid.Action.Kubernetes.Namespace") ?? "default";
        var helmExe = GetPropertyValue(ctx.Action, "Squid.Action.Helm.CustomHelmExecutable") ?? string.Empty;
        var resetValues = GetPropertyValue(ctx.Action, "Squid.Action.Helm.ResetValues") ?? "True";
        var helmWait = GetPropertyValue(ctx.Action, "Squid.Action.Helm.ClientVersion") != null ? "True" : "False";
        var additionalArgs = GetPropertyValue(ctx.Action, "Squid.Action.Helm.AdditionalArgs") ?? string.Empty;

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
            Syntax = syntax
        };

        return Task.FromResult(result);
    }

    private static string BuildValuesFilesBlock(DeploymentActionDto action, ScriptSyntax syntax, Dictionary<string, byte[]> files)
    {
        var rawYaml = GetPropertyValue(action, "Squid.Action.Helm.YamlValues");

        if (string.IsNullOrWhiteSpace(rawYaml)) return string.Empty;

        const string rawYamlFileName = "rawYamlValues.yaml";
        files[rawYamlFileName] = Encoding.UTF8.GetBytes(rawYaml);

        var sb = new StringBuilder();

        if (syntax == ScriptSyntax.Bash)
            sb.AppendLine($"HELM_ARGS=\"$HELM_ARGS --values ./{rawYamlFileName}\"");
        else
            sb.AppendLine($"$helmArgs += \"--values\"; $helmArgs += \".\\{rawYamlFileName}\"");

        return sb.ToString();
    }

    private static string BuildSetValuesBlock(DeploymentActionDto action, ScriptSyntax syntax)
    {
        var keyValuesRaw = GetPropertyValue(action, "Squid.Action.Helm.KeyValues");

        if (string.IsNullOrWhiteSpace(keyValuesRaw)) return string.Empty;

        var sb = new StringBuilder();

        try
        {
            var keyValues = JsonSerializer.Deserialize<Dictionary<string, string>>(keyValuesRaw);

            if (keyValues == null) return string.Empty;

            foreach (var kvp in keyValues)
            {
                AppendSetValue(sb, syntax, $"{kvp.Key}={kvp.Value}");
            }
        }
        catch (JsonException)
        {
            var pairs = keyValuesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var pair in pairs)
            {
                AppendSetValue(sb, syntax, pair);
            }
        }

        return sb.ToString();
    }

    private static void AppendSetValue(StringBuilder sb, ScriptSyntax syntax, string setValue)
    {
        if (syntax == ScriptSyntax.Bash)
            sb.AppendLine($"HELM_ARGS=\"$HELM_ARGS --set {setValue}\"");
        else
            sb.AppendLine($"$helmArgs += \"--set\"; $helmArgs += \"{setValue}\"");
    }

    private static string GetPropertyValue(DeploymentActionDto action, string propertyName)
    {
        return action.Properties?
            .FirstOrDefault(p => string.Equals(p.PropertyName, propertyName, StringComparison.OrdinalIgnoreCase))
            ?.PropertyValue;
    }
}
