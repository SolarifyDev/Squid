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

        if (string.IsNullOrEmpty(syntaxStr)) return ScriptSyntax.PowerShell;

        return Enum.TryParse<ScriptSyntax>(syntaxStr, ignoreCase: true, out var parsed)
            ? parsed
            : ScriptSyntax.PowerShell;
    }

    internal static bool IsShellSyntax(ScriptSyntax syntax)
        => syntax is ScriptSyntax.Bash or ScriptSyntax.PowerShell;
}
