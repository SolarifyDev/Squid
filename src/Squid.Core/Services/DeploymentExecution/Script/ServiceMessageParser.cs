using System.Text;
using System.Text.RegularExpressions;

namespace Squid.Core.Services.DeploymentExecution.Script;

public static partial class ServiceMessageParser
{
    // Base64 format: ##squid[setVariable name="<base64name>" value="<base64value>" sensitive="<base64bool>"] (also accepts ##octopus[ for backward compat)
    private static readonly Regex Base64FormatRegex = BuildBase64FormatRegex();

    [GeneratedRegex(@"^##(?:squid|octopus)\[setVariable\s+name=""(?<name>[^""]*)""\s+value=""(?<value>[^""]*)""(?:\s+sensitive=""(?<sensitive>[^""]*)"")?\]$", RegexOptions.Compiled)]
    private static partial Regex BuildBase64FormatRegex();

    // Plaintext format: ##squid[setVariable name='<plaintext>' value='<plaintext>' sensitive='True|False'] (also accepts ##octopus[ for backward compat)
    private static readonly Regex LegacyFormatRegex = BuildLegacyFormatRegex();

    [GeneratedRegex(@"^##(?:squid|octopus)\[setVariable\s+name='(?<name>[^']*?)'\s+value='(?<value>[^']*?)'(?:\s+sensitive='(?<sensitive>[^']*?)')?\]$", RegexOptions.Compiled)]
    private static partial Regex BuildLegacyFormatRegex();

    public static Dictionary<string, OutputVariable> ParseOutputVariables(IEnumerable<string> logLines)
    {
        var result = new Dictionary<string, OutputVariable>(StringComparer.OrdinalIgnoreCase);

        if (logLines == null)
            return result;

        foreach (var line in logLines)
        {
            if (string.IsNullOrEmpty(line) ||
                (!line.StartsWith("##squid[setVariable", StringComparison.Ordinal) &&
                 !line.StartsWith("##octopus[setVariable", StringComparison.Ordinal)))
                continue;

            var variable = TryParse(line);
            if (variable == null)
                continue;

            result[variable.Name] = variable;
        }

        return result;
    }

    internal static OutputVariable TryParse(string line)
    {
        var match = Base64FormatRegex.Match(line);
        if (match.Success)
            return FromBase64Match(match);

        match = LegacyFormatRegex.Match(line);
        if (match.Success)
            return FromPlaintextMatch(match);

        return null;
    }

    private static OutputVariable FromBase64Match(Match match)
    {
        try
        {
            var name = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["name"].Value));
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["value"].Value));

            var sensitive = false;
            if (match.Groups["sensitive"].Success && !string.IsNullOrEmpty(match.Groups["sensitive"].Value))
            {
                var sensitiveStr = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["sensitive"].Value));
                sensitive = string.Equals(sensitiveStr, "True", StringComparison.OrdinalIgnoreCase);
            }

            if (string.IsNullOrEmpty(name)) return null;

            return new OutputVariable(name, value, sensitive);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static OutputVariable FromPlaintextMatch(Match match)
    {
        var name = match.Groups["name"].Value;
        if (string.IsNullOrEmpty(name)) return null;

        var value = match.Groups["value"].Value;
        var sensitive = string.Equals(match.Groups["sensitive"].Value, "True", StringComparison.OrdinalIgnoreCase);

        return new OutputVariable(name, value, sensitive);
    }
}

public record OutputVariable(string Name, string Value, bool IsSensitive);
