using Squid.Message.Enums.Deployments;

namespace Squid.Message.Models.Deployments.Project;

/// <summary>
/// Project-level deployment settings. Persisted as a JSON blob on the project so the
/// shape can grow (release versioning, default failure mode, default package download,
/// etc.) without a migration per setting. Today it carries the transient-target
/// behaviour; an absent / null blob means "all defaults" — see
/// <see cref="TransientDeploymentTargetsDto"/> for what each default resolves to (the
/// unavailable-target default is fail-fast).
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
    /// <summary>
    /// Default <see cref="UnavailableDeploymentTargetBehavior.FailDeployment"/>: an
    /// unconfigured project fails fast on an unreachable target rather than silently
    /// skipping it and reporting success. A project opts back into the lenient behaviour by
    /// explicitly setting <see cref="UnavailableDeploymentTargetBehavior.SkipAndContinue"/>.
    /// System.Text.Json keeps this initializer when the field is absent, so an explicitly
    /// persisted <c>SkipAndContinue</c> is preserved and only never-configured projects pick
    /// up the fail-fast default.
    /// </summary>
    public UnavailableDeploymentTargetBehavior UnavailableDeploymentTargets { get; set; } = UnavailableDeploymentTargetBehavior.FailDeployment;

    public UnhealthyDeploymentTargetBehavior UnhealthyDeploymentTargets { get; set; }
}
