using Squid.Calamari.Commands;
using Squid.Calamari.Scripting;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands.Conventions;

/// <summary>
/// PR-7 — shared bootstrap helper for convention scripts. Generates the
/// syntax-appropriate variable preamble (bash <c>export</c> or PowerShell
/// <c>$env:</c>) and writes the bootstrapped temp file with the matching
/// extension, so a convention hook sees the same variable scope as the
/// operator's main script regardless of shell.
///
/// <para>Shared between <see cref="ConventionScriptStep"/> (Pre/Post) and
/// <see cref="DeployFailedConventionStep"/> so the bootstrap shape can't
/// drift between the normal-phase and cleanup-phase hooks.</para>
/// </summary>
internal static class ConventionBootstrap
{
    /// <summary>
    /// Read the convention script, prepend the syntax-appropriate variable
    /// preamble, write to a uniquely-named temp file in the working dir
    /// with the matching extension. Returns the temp path (caller tracks
    /// it for cleanup). Caller has already null-checked WorkingDirectory +
    /// Variables.
    /// </summary>
    public static string WriteBootstrappedConventionScript(
        RunScriptCommandContext context,
        string conventionName,
        ResolvedConvention resolved)
    {
        var originalScript = File.ReadAllText(resolved.Path);
        var preamble = resolved.Syntax switch
        {
            ScriptSyntax.PowerShell => PowerShellVariableBootstrapper.GeneratePreamble(context.Variables!),
            _ => VariableBootstrapper.GeneratePreamble(context.Variables!)
        };
        var bootstrappedScript = preamble + originalScript;

        var extension = resolved.Syntax == ScriptSyntax.PowerShell ? ".ps1" : ".sh";
        var bootstrappedPath = Path.Combine(
            context.WorkingDirectory!,
            $".squid-{conventionName.ToLowerInvariant()}-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(bootstrappedPath, bootstrappedScript);

        return bootstrappedPath;
    }
}
