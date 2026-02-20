using System.Text.RegularExpressions;

namespace Squid.Core.Services.DeploymentExecution;

public static partial class ServiceMessageParser
{
    private static readonly Regex SetVariableRegex = BuildSetVariableRegex();

    [GeneratedRegex(@"^##octopus\[setVariable\s+name='(?<name>[^']*)'\s+value='(?<value>[^']*)'(?:\s+sensitive='(?<sensitive>[^']*)')?\]$", RegexOptions.Compiled)]
    private static partial Regex BuildSetVariableRegex();

    public static Dictionary<string, OutputVariable> ParseOutputVariables(IEnumerable<string> logLines)
    {
        var result = new Dictionary<string, OutputVariable>(StringComparer.OrdinalIgnoreCase);

        if (logLines == null)
            return result;

        foreach (var line in logLines)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("##octopus[setVariable"))
                continue;

            var match = SetVariableRegex.Match(line);
            if (!match.Success)
                continue;

            var name = match.Groups["name"].Value;
            if (string.IsNullOrEmpty(name))
                continue;

            var value = match.Groups["value"].Value;
            var sensitive = string.Equals(
                match.Groups["sensitive"].Value, "True", StringComparison.OrdinalIgnoreCase);

            result[name] = new OutputVariable(name, value, sensitive);
        }

        return result;
    }
}

public record OutputVariable(string Name, string Value, bool IsSensitive);
