using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Intents;

/// <summary>
/// Intent to run a user-authored script on the target. The script body is the raw,
/// variable-substituted source — the renderer adds bootstrap (variable exports, runtime
/// bundle, context wrappers) based on the transport.
/// </summary>
public sealed record RunScriptIntent : ExecutionIntent
{
    /// <summary>The script body as authored by the user, with deployment variables already substituted.</summary>
    public required string ScriptBody { get; init; }

    /// <summary>The syntax/language of <see cref="ScriptBody"/> (bash, PowerShell, Python, ...).</summary>
    public ScriptSyntax Syntax { get; init; } = ScriptSyntax.Bash;

    /// <summary>
    /// When true, the renderer must inject the <c>squid-runtime.sh</c> / <c>squid-runtime.ps1</c>
    /// helper bundle (Phase 8) so that <c>set_squidvariable</c>, <c>new_squidartifact</c>, and
    /// <c>fail_step</c> are available in the script. Default: <c>true</c>.
    /// </summary>
    public bool InjectRuntimeBundle { get; init; } = true;
}
