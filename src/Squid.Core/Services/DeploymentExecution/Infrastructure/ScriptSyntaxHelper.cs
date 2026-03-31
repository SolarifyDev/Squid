using Squid.Core.Extensions;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

internal static class ScriptSyntaxHelper
{
    internal static ScriptSyntax ResolveSyntax(DeploymentActionDto action)
    {
        var syntaxStr = action.GetProperty(SpecialVariables.Action.ScriptSyntax);

        if (string.IsNullOrEmpty(syntaxStr)) return ScriptSyntax.Bash;

        if (int.TryParse(syntaxStr, out _))
        {
            Log.Warning("Numeric ScriptSyntax value '{SyntaxValue}' is not supported — defaulting to Bash. Use string names: Bash, PowerShell, CSharp, FSharp, Python", syntaxStr);
            return ScriptSyntax.Bash;
        }

        if (Enum.TryParse<ScriptSyntax>(syntaxStr, ignoreCase: true, out var parsed))
            return parsed;

        Log.Warning("Unknown ScriptSyntax value '{SyntaxValue}' — defaulting to Bash", syntaxStr);
        return ScriptSyntax.Bash;
    }

    internal static bool IsShellSyntax(ScriptSyntax syntax)
        => syntax is ScriptSyntax.Bash or ScriptSyntax.PowerShell;
}
