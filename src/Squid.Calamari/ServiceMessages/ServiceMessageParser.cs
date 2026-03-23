using System.Text.RegularExpressions;

namespace Squid.Calamari.ServiceMessages;

/// <summary>
/// Parses ##squid[setVariable ...] (and ##octopus[setVariable ...] for backward compat) service messages from script output lines.
/// </summary>
public static partial class ServiceMessageParser
{
    [GeneratedRegex(@"^##(?:squid|octopus)\[setVariable\s+name='(?<name>[^']*)'\s+value='(?<value>[^']*)'(?:\s+sensitive='(?<sensitive>[^']*)')?\]$",
        RegexOptions.Compiled)]
    private static partial Regex BuildSetVariableRegex();

    private static readonly Regex SetVariableRegex = BuildSetVariableRegex();

    public static bool IsServiceMessage(string line)
        => !string.IsNullOrEmpty(line) &&
           (line.StartsWith("##squid[", StringComparison.Ordinal) ||
            line.StartsWith("##octopus[", StringComparison.Ordinal));

    public static OutputVariable? TryParse(string line)
    {
        if (!IsServiceMessage(line))
            return null;

        var match = SetVariableRegex.Match(line);

        if (!match.Success)
            return null;

        var name = match.Groups["name"].Value;

        if (string.IsNullOrEmpty(name))
            return null;

        var value = match.Groups["value"].Value;
        var sensitive = string.Equals(
            match.Groups["sensitive"].Value, "True", StringComparison.OrdinalIgnoreCase);

        return new OutputVariable(name, value, sensitive);
    }
}

public record OutputVariable(string Name, string Value, bool IsSensitive);
