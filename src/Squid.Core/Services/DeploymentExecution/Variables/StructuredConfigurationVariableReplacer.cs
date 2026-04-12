using Serilog;
using Squid.Core.VariableSubstitution;
using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Variables;

internal static class StructuredConfigurationVariableReplacer
{
    private static readonly string[] ReservedPrefixes = { "Squid.", "System." };

    internal static bool IsEnabled(Dictionary<string, string> actionProperties)
    {
        if (actionProperties == null) return false;

        if (!actionProperties.TryGetValue(SpecialVariables.Action.StructuredConfigurationVariablesEnabled, out var value))
            return false;

        return string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
    }

    internal static Dictionary<string, string> BuildReplacementMap(VariableDictionary variableDictionary)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in variableDictionary.GetNames())
        {
            if (IsReservedVariable(name)) continue;

            var value = variableDictionary.GetRaw(name);
            if (value != null)
                map[name] = value;
        }

        return map;
    }

    private static void ReplaceInFiles(Dictionary<string, byte[]> files, Dictionary<string, string> replacements, List<string> warnings)
    {
        foreach (var key in files.Keys.ToList())
        {
            try
            {
                if (IsJsonFile(key))
                    files[key] = JsonStructuredVariableReplacer.ReplaceInJsonFile(files[key], replacements);
                else if (IsYamlFile(key))
                    files[key] = YamlStructuredVariableReplacer.ReplaceInYamlFile(files[key], replacements);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Deploy] Structured variable replacement failed for file {FileName}, skipping", key);
                warnings.Add($"Structured variable replacement failed for '{key}': {ex.Message}");
            }
        }
    }

    internal static bool IsReservedVariable(string name)
    {
        return ReservedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsJsonFile(string fileName)
        => fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static bool IsYamlFile(string fileName)
        => fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
}
