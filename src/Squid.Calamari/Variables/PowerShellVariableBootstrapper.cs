using System.Text;

namespace Squid.Calamari.Variables;

/// <summary>
/// PR-4 — PowerShell counterpart to <see cref="VariableBootstrapper"/>.
/// Generates a <c>$env:VAR = 'value'</c> preamble that injects variables
/// into the PowerShell environment, so operator scripts see the same
/// variable scope they'd see on bash.
///
/// <para><b>UTF-8 stdout pin</b>: prepended before the env-var block.
/// On Windows, both <c>powershell.exe</c> and (some) <c>pwsh</c> hosts
/// default <c>$OutputEncoding</c> + <c>[Console]::OutputEncoding</c> to
/// the OEM codepage. Without this pin, non-ASCII chars (Chinese / emoji
/// / curly quotes) get mangled before they hit the captured-log pipe.
/// Same approach as the Tentacle-side <c>LocalScriptService.PowerShellUtf8Preamble</c>.</para>
///
/// <para><b>Variable-name sanitisation</b>: PowerShell env-var names
/// accept letters, digits, underscores. We strip the same forbidden
/// chars as the bash bootstrapper (<c>.</c>, <c>-</c>, <c>/</c>) so the
/// SAME variable name resolves under either shell. Operator who switched
/// from <c>.sh</c> to <c>.ps1</c> doesn't have to relearn naming.</para>
///
/// <para><b>Value escaping</b>: single-quoted PowerShell literals only
/// need <c>'</c> escaped as <c>''</c>. Embedded newlines, dollars,
/// backticks all survive literally — same lossless behaviour as the bash
/// bootstrapper's single-quote wrapping after the B.6 audit.</para>
/// </summary>
public static class PowerShellVariableBootstrapper
{
    public static string GeneratePreamble(IEnumerable<KeyValuePair<string, string>> variables)
    {
        var sb = new StringBuilder();

        // Force UTF-8 stdout BEFORE anything else runs — same line we use
        // in the Tentacle script wrapper. Single line keeps the line-number
        // shift small so error messages from operator scripts still point
        // at meaningful lines (header + utf8 + var-count lines).
        sb.AppendLine("# Squid: force UTF-8 stdout so non-ASCII chars (Chinese / emoji) round-trip through the captured-log layer.");
        sb.AppendLine("$OutputEncoding=[System.Text.UTF8Encoding]::new($false);[Console]::OutputEncoding=$OutputEncoding");
        sb.AppendLine();

        foreach (var (name, value) in variables)
        {
            if (!IsValidPowerShellVariableName(name))
                continue;

            var envName = SanitizeName(name);
            var escapedValue = EscapeSingleQuoted(value ?? string.Empty);

            // $env:VAR = '...' — exposes to child processes; matches what
            // `export VAR=...` does on bash.
            sb.AppendLine($"$env:{envName} = '{escapedValue}'");
        }

        sb.AppendLine();

        return sb.ToString();
    }

    private static bool IsValidPowerShellVariableName(string name)
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
    /// PowerShell single-quote literal escape: ONLY the single-quote needs
    /// doubling (<c>'</c> → <c>''</c>). Everything else survives literally,
    /// including <c>$</c>, <c>`</c>, embedded newlines, double quotes.
    /// </summary>
    private static string EscapeSingleQuoted(string value)
        => value.Replace("'", "''");
}
