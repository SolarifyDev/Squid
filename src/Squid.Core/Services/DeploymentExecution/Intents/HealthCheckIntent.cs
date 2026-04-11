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

    /// <summary>
    /// Depth of the health check. <see cref="HealthCheckType.FullHealthCheck"/> runs the
    /// machine-policy probe (typically a scripted check); <see cref="HealthCheckType.ConnectionTest"/>
    /// only verifies the transport is reachable.
    /// </summary>
    public HealthCheckType CheckType { get; init; } = HealthCheckType.FullHealthCheck;

    /// <summary>
    /// How the pipeline should react to failed probes.
    /// <see cref="HealthCheckErrorHandling.FailDeployment"/> aborts the deployment;
    /// <see cref="HealthCheckErrorHandling.SkipUnavailable"/> logs a warning and excludes
    /// the unhealthy targets from subsequent steps.
    /// </summary>
    public HealthCheckErrorHandling ErrorHandling { get; init; } = HealthCheckErrorHandling.FailDeployment;

    /// <summary>
    /// When true, the pipeline re-queries the deployment target finder after the probe and
    /// adds any newly-discovered targets to the run. Maps to the legacy
    /// <c>"IncludeCheckedMachines"</c> property value.
    /// </summary>
    public bool IncludeNewTargets { get; init; }
}

/// <summary>Depth of a <see cref="HealthCheckIntent"/> probe.</summary>
public enum HealthCheckType
{
    /// <summary>Full health check — runs the machine-policy probe (typically a scripted check).</summary>
    FullHealthCheck,

    /// <summary>Connection test only — verifies the transport is reachable.</summary>
    ConnectionTest
}

/// <summary>Failure policy for a <see cref="HealthCheckIntent"/> probe.</summary>
public enum HealthCheckErrorHandling
{
    /// <summary>Abort the deployment when any target is unhealthy.</summary>
    FailDeployment,

    /// <summary>Log a warning and exclude unhealthy targets from subsequent steps.</summary>
    SkipUnavailable
}
