using Squid.Core.Services.Deployments.Project;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Machine = Squid.Core.Persistence.Entities.Deployments.Machine;

namespace Squid.Core.Services.DeploymentExecution.Filtering;

/// <summary>
/// Outcome of applying a project's "Transient Deployment Targets" setting to the
/// candidate targets. <see cref="Kept"/> proceed; <see cref="Skipped"/> are removed
/// from the deployment (unhealthy-excluded and/or unavailable-skipped) and announced;
/// <see cref="FailedUnavailable"/> are unavailable targets whose policy is
/// <see cref="UnavailableDeploymentTargetBehavior.FailDeployment"/> — a non-empty list
/// means the caller must fail the deployment.
/// </summary>
public sealed record TransientTargetResult(
    List<Machine> Kept,
    List<Machine> Skipped,
    List<Machine> FailedUnavailable);

/// <summary>
/// Single source of truth for the project "Transient Deployment Targets" behaviour.
/// Pure + deterministic so it can be unit-tested exhaustively.
///
/// <para>For an unconfigured project the defaults are
/// <see cref="UnavailableDeploymentTargetBehavior.FailDeployment"/> (an unavailable target
/// fails the deployment up front rather than being silently skipped while the task reports
/// success) and <see cref="UnhealthyDeploymentTargetBehavior.Exclude"/> (unhealthy targets
/// are dropped). Explicit <see cref="UnavailableDeploymentTargetBehavior.SkipAndContinue"/> /
/// <see cref="UnhealthyDeploymentTargetBehavior.DoNotExclude"/> opt back into the lenient
/// behaviour. The default is applied at the settings DTO, not by enum ordinal.</para>
/// </summary>
public static class TransientDeploymentTargetEvaluator
{
    /// <summary>
    /// Resolves the project's "Transient Deployment Targets" policy from its
    /// deployment-settings JSON and applies it. This is the single entry point both the
    /// deployment pipeline (phase 4) and the deployment preview call, so the two cannot
    /// disagree about which targets are eligible.
    /// </summary>
    public static TransientTargetResult ApplyProjectPolicy(IReadOnlyList<Machine> candidates, string deploymentSettingsJson)
    {
        var transient = DeploymentSettingsSerializer.Deserialize(deploymentSettingsJson).TransientDeploymentTargets;

        return Apply(candidates, transient.UnavailableDeploymentTargets, transient.UnhealthyDeploymentTargets);
    }

    /// <summary>
    /// Role-scoped policy application: the candidate set is first narrowed to the
    /// targets that match at least one of the deployment's step roles. A target that
    /// matches NO step role is not a deployment target for this release, so it must
    /// neither fail the deployment (under the 'Fail deployment' policy) nor be
    /// reported as excluded — it is simply irrelevant. An empty
    /// <paramref name="requiredRoles"/> means some enabled step targets all machines,
    /// so every candidate is relevant (no narrowing). Both the pipeline (phase 4) and
    /// the preview call THIS overload, so neither can apply the policy to targets the
    /// deployment would never touch.
    /// </summary>
    public static TransientTargetResult ApplyProjectPolicy(IReadOnlyList<Machine> candidates, HashSet<string> requiredRoles, string deploymentSettingsJson)
    {
        var relevant = DeploymentTargetFinder.FilterByRoles(candidates.ToList(), requiredRoles);

        return ApplyProjectPolicy(relevant, deploymentSettingsJson);
    }

    public static TransientTargetResult Apply(
        IReadOnlyList<Machine> candidates,
        UnavailableDeploymentTargetBehavior unavailableBehavior,
        UnhealthyDeploymentTargetBehavior unhealthyBehavior)
    {
        var kept = new List<Machine>();
        var skipped = new List<Machine>();
        var failedUnavailable = new List<Machine>();

        foreach (var machine in candidates)
        {
            switch (machine.HealthStatus)
            {
                case MachineHealthStatus.Unavailable when unavailableBehavior == UnavailableDeploymentTargetBehavior.FailDeployment:
                    failedUnavailable.Add(machine);
                    break;

                case MachineHealthStatus.Unavailable:
                    skipped.Add(machine); // SkipAndContinue (default)
                    break;

                case MachineHealthStatus.Unhealthy when unhealthyBehavior == UnhealthyDeploymentTargetBehavior.DoNotExclude:
                    kept.Add(machine);
                    break;

                case MachineHealthStatus.Unhealthy:
                    skipped.Add(machine); // Exclude (default)
                    break;

                default:
                    kept.Add(machine); // Healthy / Unknown / HasWarnings — unchanged
                    break;
            }
        }

        return new TransientTargetResult(kept, skipped, failedUnavailable);
    }
}
