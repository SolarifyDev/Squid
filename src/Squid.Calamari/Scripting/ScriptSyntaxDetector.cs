namespace Squid.Calamari.Scripting;

/// <summary>
/// PR-4 — detect <see cref="ScriptSyntax"/> from a script's file
/// extension. Operator-friendly: name your file <c>.ps1</c> for
/// PowerShell, <c>.sh</c> (or anything else) for bash. No new wire
/// literal needed — the extension IS the hint.
///
/// <para><b>Fallback to Bash</b> when the extension is unknown or empty.
/// Existing operators who pass <c>script-{guid}.sh</c> (or no extension
/// at all) keep the bash code path — back-compat with everything pre-PR-4.</para>
/// </summary>
public static class ScriptSyntaxDetector
{
    /// <summary>
    /// Map file extension → syntax. Case-insensitive. Returns
    /// <see cref="ScriptSyntax.Bash"/> for unknowns (preserves existing
    /// bash-only behaviour).
    /// </summary>
    public static ScriptSyntax DetectFromPath(string scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath)) return ScriptSyntax.Bash;

        var ext = Path.GetExtension(scriptPath);
        if (string.IsNullOrEmpty(ext)) return ScriptSyntax.Bash;

        return ext.ToLowerInvariant() switch
        {
            ".ps1" => ScriptSyntax.PowerShell,
            ".psm1" => ScriptSyntax.PowerShell,
            // .sh and everything else → bash (default + back-compat).
            _ => ScriptSyntax.Bash
        };
    }
}
