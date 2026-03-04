using System.Text;

namespace Squid.Calamari.Variables;

/// <summary>
/// Generates a bash export preamble that injects variables into the script execution environment.
/// Variable names are sanitized: dots and hyphens replaced with underscores.
/// </summary>
public static class VariableBootstrapper
{
    public static string GeneratePreamble(IEnumerable<KeyValuePair<string, string>> variables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("set -e");
        sb.AppendLine();

        foreach (var (name, value) in variables)
        {
            if (string.IsNullOrEmpty(name))
                continue;

            var envName = SanitizeName(name);
            var escapedValue = EscapeValue(value ?? string.Empty);

            sb.AppendLine($"export {envName}={escapedValue}");
        }

        sb.AppendLine();

        return sb.ToString();
    }

    private static string SanitizeName(string name)
        => name.Replace('.', '_').Replace('-', '_').Replace('/', '_');

    private static string EscapeValue(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

        return $"\"{escaped}\"";
    }
}
