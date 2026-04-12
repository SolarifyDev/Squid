using Serilog;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Core.VariableSubstitution;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Variables;

/// <summary>
/// Applies structured variable replacement (JSON/YAML key-matching) to file collections
/// on <see cref="ExecutionIntent"/> records. Mirrors the legacy
/// <see cref="StructuredConfigurationVariableReplacer.ReplaceIfEnabled"/> but operates on
/// immutable <see cref="DeploymentFile"/> collections instead of mutable
/// <c>Dictionary&lt;string, byte[]&gt;</c>.
///
/// <para>Only <see cref="KubernetesApplyIntent.YamlFiles"/> and
/// <see cref="HelmUpgradeIntent.ValuesFiles"/> currently carry replaceable file content.</para>
/// </summary>
internal static class IntentStructuredConfigReplacer
{
    internal static (ExecutionIntent Intent, List<string> Warnings) ReplaceIfEnabled(ExecutionIntent intent, DeploymentActionDto action, VariableDictionary variableDictionary)
    {
        var warnings = new List<string>();

        if (!IsEnabled(action))
            return (intent, warnings);

        var files = ExtractFiles(intent);

        if (files == null || files.Count == 0)
            return (intent, warnings);

        var replacements = StructuredConfigurationVariableReplacer.BuildReplacementMap(variableDictionary);

        if (replacements.Count == 0)
            return (intent, warnings);

        var replacedFiles = ReplaceInDeploymentFiles(files, replacements, warnings);

        return (ApplyReplacedFiles(intent, replacedFiles), warnings);
    }

    private static bool IsEnabled(DeploymentActionDto action)
    {
        if (action?.Properties == null) return false;

        var value = action.Properties
            .FirstOrDefault(p => string.Equals(p.PropertyName, SpecialVariables.Action.StructuredConfigurationVariablesEnabled, StringComparison.OrdinalIgnoreCase))
            ?.PropertyValue;

        return string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<DeploymentFile> ExtractFiles(ExecutionIntent intent)
    {
        return intent switch
        {
            KubernetesApplyIntent k8s => k8s.YamlFiles,
            HelmUpgradeIntent helm => helm.ValuesFiles,
            _ => null
        };
    }

    private static ExecutionIntent ApplyReplacedFiles(ExecutionIntent intent, IReadOnlyList<DeploymentFile> replacedFiles)
    {
        return intent switch
        {
            KubernetesApplyIntent k8s => k8s with { YamlFiles = replacedFiles },
            HelmUpgradeIntent helm => helm with { ValuesFiles = replacedFiles },
            _ => intent
        };
    }

    private static IReadOnlyList<DeploymentFile> ReplaceInDeploymentFiles(IReadOnlyList<DeploymentFile> files, Dictionary<string, string> replacements, List<string> warnings)
    {
        var result = new List<DeploymentFile>(files.Count);

        foreach (var file in files)
        {
            var replaced = TryReplace(file, replacements, warnings);
            result.Add(replaced);
        }

        return result;
    }

    private static DeploymentFile TryReplace(DeploymentFile file, Dictionary<string, string> replacements, List<string> warnings)
    {
        try
        {
            if (IsJsonFile(file.RelativePath))
            {
                var newContent = JsonStructuredVariableReplacer.ReplaceInJsonFile(file.Content, replacements);
                return file with { Content = newContent };
            }

            if (IsYamlFile(file.RelativePath))
            {
                var newContent = YamlStructuredVariableReplacer.ReplaceInYamlFile(file.Content, replacements);
                return file with { Content = newContent };
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Structured variable replacement failed for file {FileName}, skipping", file.RelativePath);
            warnings.Add($"Structured variable replacement failed for '{file.RelativePath}': {ex.Message}");
        }

        return file;
    }

    private static bool IsJsonFile(string fileName)
        => fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static bool IsYamlFile(string fileName)
        => fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
}
