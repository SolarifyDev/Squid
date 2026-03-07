using Squid.Core.Services.Deployments.Validation;
using Squid.Message.Models.Deployments.Deployment;

namespace Squid.Core.Services.Deployments.Deployments;

public partial class DeploymentService
{
    public async Task<DeploymentEnvironmentValidationResult> ValidateDeploymentEnvironmentAsync(DeploymentValidationContext context, CancellationToken cancellationToken = default)
    {
        Log.Information("Validating deployment environment for release {ReleaseId} and environment {EnvironmentId}", context.ReleaseId, context.EnvironmentId);

        var result = new DeploymentEnvironmentValidationResult();

        var release = await _releaseDataProvider
            .GetReleaseByIdAsync(context.ReleaseId, cancellationToken).ConfigureAwait(false);

        if (release == null)
        {
            Log.Warning("Release {ReleaseId} not found", context.ReleaseId);
            result.Reasons.Add($"Release {context.ReleaseId} not found.");
            return result;
        }

        var environment = await _environmentDataProvider
            .GetEnvironmentByIdAsync(context.EnvironmentId, cancellationToken).ConfigureAwait(false);

        if (environment == null)
        {
            Log.Warning("Environment {EnvironmentId} not found", context.EnvironmentId);
            result.Reasons.Add($"Environment {context.EnvironmentId} not found.");
            return result;
        }

        var machines = await _machineDataProvider
            .GetMachinesByFilterAsync([context.EnvironmentId], new HashSet<string>(), cancellationToken).ConfigureAwait(false);

        var selectedMachines = ApplyMachineSelection(machines, context.SpecificMachineIds, context.ExcludedMachineIds);
        
        result.AvailableMachineCount = selectedMachines.Count;

        if (selectedMachines.Count == 0)
        {
            Log.Warning("No available machines found in environment {EnvironmentId}", context.EnvironmentId);

            if (context.SpecificMachineIds.Count > 0 || context.ExcludedMachineIds.Count > 0)
            {
                result.Reasons.Add($"No available machines found after applying machine selection constraints in environment {context.EnvironmentId}.");
            }
            else
            {
                result.Reasons.Add($"No available machines found in environment {context.EnvironmentId}.");
            }
        }

        var lifecycle = await _lifecycleResolver
            .ResolveLifecycleAsync(release.ProjectId, release.ChannelId, cancellationToken).ConfigureAwait(false);

        result.LifecycleId = lifecycle.Id;

        var progression = await _progressionEvaluator
            .EvaluateProgressionAsync(lifecycle.Id, release.ProjectId, cancellationToken).ConfigureAwait(false);

        result.AllowedEnvironmentIds = progression.AllowedEnvironmentIds;

        if (!progression.AllowedEnvironmentIds.Contains(context.EnvironmentId))
        {
            Log.Warning("Environment {EnvironmentId} is not allowed by lifecycle {LifecycleId} progression", context.EnvironmentId, lifecycle.Id);

            var allowedText = progression.AllowedEnvironmentIds.Count == 0
                ? "<none>"
                : string.Join(", ", progression.AllowedEnvironmentIds);

            result.Reasons.Add($"Environment {context.EnvironmentId} is not allowed by lifecycle {lifecycle.Id} progression. Allowed: {allowedText}.");
        }

        result.IsValid = result.Reasons.Count == 0;

        if (result.IsValid)
            Log.Information("Environment validation passed: found {MachineCount} available machines", selectedMachines.Count);

        return result;
    }

    private static List<Persistence.Entities.Deployments.Machine> ApplyMachineSelection(List<Persistence.Entities.Deployments.Machine> machines, HashSet<int> specificMachineIds, HashSet<int> excludedMachineIds)
    {
        if (machines.Count == 0)
            return machines;

        var selected = machines;

        if (specificMachineIds.Count > 0)
            selected = selected.Where(m => specificMachineIds.Contains(m.Id)).ToList();

        if (excludedMachineIds.Count > 0)
            selected = selected.Where(m => !excludedMachineIds.Contains(m.Id)).ToList();

        return selected;
    }
}
