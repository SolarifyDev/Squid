namespace Squid.Core.Services.DeploymentExecution.Runtime;

/// <summary>
/// Identifies which scripting language runtime a bundle targets. A single bundle
/// is selected per user script based on its <see cref="Message.Models.Deployments.Execution.ScriptSyntax"/>.
/// </summary>
public enum RuntimeBundleKind
{
    Bash = 0,
    PowerShell = 1,
    Python = 2
}
