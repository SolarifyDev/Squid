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
/// <para>Defaults (<see cref="UnavailableDeploymentTargetBehavior.SkipAndContinue"/> +
/// <see cref="UnhealthyDeploymentTargetBehavior.Exclude"/>) reproduce Squid's historical
/// unconditional exclusion of unavailable + unhealthy targets, so honouring the setting
/// is non-breaking for projects that haven't configured it.</para>
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
