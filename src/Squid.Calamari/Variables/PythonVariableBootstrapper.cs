using System.Text;

namespace Squid.Calamari.Variables;

/// <summary>
/// PR-10 — Python counterpart to <see cref="VariableBootstrapper"/> (bash)
/// and <see cref="PowerShellVariableBootstrapper"/> (PS). Generates an
/// <c>os.environ['VAR'] = '...'</c> preamble so an operator's Python script
/// reads variables via <c>os.environ</c> / <c>os.getenv</c> — same
/// environment-variable injection model as bash <c>export</c> and PS
/// <c>$env:</c>.
///
/// <para><b>Value escaping</b>: each value is emitted as a single-quoted
/// Python string literal. Python single-quoted literals interpret backslash
/// escapes, so we escape (in order) <c>\</c> → <c>\\</c>, <c>'</c> →
/// <c>\'</c>, newline → <c>\n</c>, CR → <c>\r</c>, tab → <c>\t</c>. This is
/// lossless (Python re-expands the escapes to the exact original bytes) AND
/// readable (operator debugging the bootstrapped script sees real text, not
/// base64). PEM keys / multi-line values round-trip correctly via the
/// <c>\n</c> escape.</para>
///
/// <para><b>Name sanitisation</b>: identical to the bash / PS bootstrappers
/// (<c>.</c> / <c>-</c> / <c>/</c> → <c>_</c>) so the SAME operator variable
/// name resolves under any of the three shells without renaming.</para>
/// </summary>
public static class PythonVariableBootstrapper
{
    public static string GeneratePreamble(IEnumerable<KeyValuePair<string, string>> variables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Squid: inject deployment variables into os.environ for the operator script.");
        sb.AppendLine("import os");
        sb.AppendLine();

        foreach (var (name, value) in variables)
        {
            if (!IsValidPythonEnvName(name))
                continue;

            var envName = SanitizeName(name);
            var escaped = EscapeSingleQuoted(value ?? string.Empty);

            sb.AppendLine($"os.environ['{envName}'] = '{escaped}'");
        }

        sb.AppendLine();

        return sb.ToString();
    }

    private static bool IsValidPythonEnvName(string name)
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
    /// Escape for a Python single-quoted string literal. Backslash FIRST
    /// (so we don't double-escape the escapes we add after), then quote +
    /// the whitespace controls Python interprets as escapes.
    /// </summary>
    private static string EscapeSingleQuoted(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
}
