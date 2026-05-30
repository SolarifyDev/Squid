using Squid.Message.Enums.Deployments;

namespace Squid.Message.Models.Deployments.Project;

/// <summary>
/// Project-level deployment settings. Persisted as a JSON blob on the project so the
/// shape can grow (release versioning, default failure mode, default package download,
/// etc.) without a migration per setting. Today it carries the transient-target
/// behaviour; an absent / null blob means "all defaults", which preserve today's
/// runtime behaviour.
/// </summary>
public class DeploymentSettingsDto
{
    public TransientDeploymentTargetsDto TransientDeploymentTargets { get; set; } = new();
}

/// <summary>
/// How a deployment treats targets that are unavailable or unhealthy when it starts.
/// Mirrors the project "Transient Deployment Targets" setting.
/// </summary>
public class TransientDeploymentTargetsDto
{
    public UnavailableDeploymentTargetBehavior UnavailableDeploymentTargets { get; set; }

    public UnhealthyDeploymentTargetBehavior UnhealthyDeploymentTargets { get; set; }
}
