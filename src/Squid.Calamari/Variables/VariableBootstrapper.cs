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
            if (!IsValidBashVariableName(name))
                continue;

            var envName = SanitizeName(name);
            var escapedValue = EscapeValue(value ?? string.Empty);

            sb.AppendLine($"export {envName}={escapedValue}");
        }

        sb.AppendLine();

        return sb.ToString();
    }

    private static bool IsValidBashVariableName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        var sanitized = SanitizeName(name);
        if (sanitized.Length == 0) return false;
        if (char.IsDigit(sanitized[0])) return false;

        return sanitized.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static string SanitizeName(string name)
        => name.Replace('.', '_').Replace('-', '_').Replace('/', '_');

    private static string EscapeValue(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

        return $"\"{escaped}\"";
    }
}
