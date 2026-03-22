using System.Text;
using Squid.Core.VariableSubstitution;

namespace Squid.Core.Services.DeploymentExecution.Variables;

internal static class YamlVariableSubstitution
{
    internal static Dictionary<string, byte[]> SubstituteInFiles(Dictionary<string, byte[]> files, VariableDictionary variables)
    {
        if (files == null || variables == null) return files;

        var result = new Dictionary<string, byte[]>(files);

        foreach (var kvp in result.ToList())
        {
            if (!IsYamlFile(kvp.Key)) continue;

            var content = Encoding.UTF8.GetString(kvp.Value);
            var substituted = variables.Evaluate(content);

            if (substituted != content)
                result[kvp.Key] = Encoding.UTF8.GetBytes(substituted);
        }

        return result;
    }

    private static bool IsYamlFile(string fileName)
        => fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
}
