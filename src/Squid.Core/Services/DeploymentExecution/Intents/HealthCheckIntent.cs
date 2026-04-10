using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Intents;

/// <summary>
/// Intent for a target health-check action. By default the renderer runs the transport's
/// built-in probe (e.g. SSH <c>echo</c>, kubectl ping, OpenClaw wake). When
/// <see cref="CustomScript"/> is non-null, the renderer runs that script instead and
/// treats its exit code as the health result.
/// </summary>
public sealed record HealthCheckIntent : ExecutionIntent
{
    /// <summary>Optional custom health-check script. When null, the transport's default probe is used.</summary>
    public string? CustomScript { get; init; }

    /// <summary>Syntax of <see cref="CustomScript"/>. Ignored when <see cref="CustomScript"/> is null.</summary>
    public ScriptSyntax Syntax { get; init; } = ScriptSyntax.Bash;
}
