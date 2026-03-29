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

        return string.Equals(syntaxStr, ScriptSyntax.Bash.ToString(), StringComparison.OrdinalIgnoreCase)
            ? ScriptSyntax.Bash
            : ScriptSyntax.PowerShell;
    }
}
