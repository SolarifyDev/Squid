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

    /// <summary>
    /// Returns a fully-quoted POSIX bash literal of <paramref name="value"/>.
    /// Caller appends directly with no surrounding quotes — the function
    /// wraps with single quotes itself.
    ///
    /// <para><b>P1-Phase-7 audit follow-up to B.6</b>: pre-fix this used
    /// double-quote wrapping with backslash escapes for <c>"</c>, <c>$</c>,
    /// <c>`</c>, <c>\</c>, and replaced <c>\n</c> / <c>\r</c> / <c>\t</c>
    /// with their literal-text two-char forms. The literal-text forms made
    /// embedded-newline injection harmless BUT lossy — the operator's
    /// actual newline value got mangled. The server-side
    /// <c>BashRuntimeBundle.EscapeBashValue</c> was migrated in B.6 to
    /// single-quote wrapping; this file (the agent-side bootstrapper used
    /// by Calamari for every Run-Script-style execution) was missed.
    /// Phase-7 audit caught it. Both sides now use the same single-quote
    /// strategy: every metacharacter inside the quote is literal; only
    /// <c>'</c> itself needs the four-character POSIX idiom <c>'\''</c>.</para>
    /// </summary>
    internal static string EscapeValue(string value)
    {
        var inner = (value ?? string.Empty).Replace("'", "'\\''", StringComparison.Ordinal);
        return "'" + inner + "'";
    }
}
