using Squid.Calamari.Scripting;

namespace Squid.Calamari.Commands.Conventions;

/// <summary>
/// PR-7 — multi-shell convention-script resolution. Operators can ship a
/// convention as <c>PreDeploy.sh</c> (bash) OR <c>PreDeploy.ps1</c>
/// (PowerShell). This resolver finds which exists and pairs it with the
/// right <see cref="ScriptSyntax"/> so the calling step dispatches to the
/// correct executor.
///
/// <para><b>Both-exist tie-break</b>: a cross-platform package may ship
/// BOTH <c>PreDeploy.sh</c> and <c>PreDeploy.ps1</c>. We prefer the one
/// matching the operator's MAIN script syntax (derived from the main
/// script's extension via <see cref="ScriptSyntaxDetector"/>). Rationale:
/// an operator deploying a PowerShell app wants the PowerShell convention;
/// a bash app wants the bash convention. Deterministic + matches intuition
/// + no OS-mocking needed in tests.</para>
///
/// <para><b>Back-compat</b>: a package shipping only <c>PreDeploy.sh</c>
/// (every pre-PR-7 package) resolves to bash exactly as before. The .ps1
/// probe is purely additive.</para>
/// </summary>
internal static class ConventionScriptResolver
{
    /// <summary>
    /// Resolve a convention script in <paramref name="workingDir"/>.
    /// Returns the absolute path + its syntax, or <see langword="null"/>
    /// when no <c>{conventionName}.sh</c> / <c>.ps1</c> exists.
    /// </summary>
    public static ResolvedConvention? Resolve(string workingDir, string conventionName, ScriptSyntax preferredSyntax)
    {
        var psPath = Path.Combine(workingDir, $"{conventionName}.ps1");
        var shPath = Path.Combine(workingDir, $"{conventionName}.sh");

        var psExists = File.Exists(psPath);
        var shExists = File.Exists(shPath);

        if (!psExists && !shExists) return null;

        // Both shipped → main script's syntax wins.
        if (psExists && shExists)
            return preferredSyntax == ScriptSyntax.PowerShell
                ? new ResolvedConvention(psPath, ScriptSyntax.PowerShell)
                : new ResolvedConvention(shPath, ScriptSyntax.Bash);

        return psExists
            ? new ResolvedConvention(psPath, ScriptSyntax.PowerShell)
            : new ResolvedConvention(shPath, ScriptSyntax.Bash);
    }
}

internal readonly record struct ResolvedConvention(string Path, ScriptSyntax Syntax);
