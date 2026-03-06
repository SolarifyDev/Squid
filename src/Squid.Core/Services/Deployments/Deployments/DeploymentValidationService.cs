using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Machines;
using Squid.Message.Models.Deployments.Deployment;

namespace Squid.Core.Services.Deployments.Deployments;

public interface IDeploymentValidationService : IScopedDependency
{
    Task<bool> ValidateDeploymentEnvironmentAsync(int releaseId, int environmentId, CancellationToken cancellationToken = default);

    Task<DeploymentEnvironmentValidationResult> ValidateDeploymentEnvironmentDetailedAsync(int releaseId, int environmentId, CancellationToken cancellationToken = default);

    Task<DeploymentEnvironmentValidationResult> ValidateDeploymentEnvironmentDetailedAsync(int releaseId, int environmentId, HashSet<int> specificMachineIds, HashSet<int> excludedMachineIds, CancellationToken cancellationToken = default);
}

public class DeploymentValidationService : IDeploymentValidationService
{
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IEnvironmentDataProvider _environmentDataProvider;
    private readonly IMachineDataProvider _machineDataProvider;
    private readonly ILifecycleResolver _lifecycleResolver;
    private readonly ILifecycleProgressionEvaluator _progressionEvaluator;

    public DeploymentValidationService(
        IReleaseDataProvider releaseDataProvider,
        IEnvironmentDataProvider environmentDataProvider,
        IMachineDataProvider machineDataProvider,
        ILifecycleResolver lifecycleResolver,
        ILifecycleProgressionEvaluator progressionEvaluator)
    {
        _releaseDataProvider = releaseDataProvider;
        _environmentDataProvider = environmentDataProvider;
        _machineDataProvider = machineDataProvider;
        _lifecycleResolver = lifecycleResolver;
        _progressionEvaluator = progressionEvaluator;
    }

    public async Task<bool> ValidateDeploymentEnvironmentAsync(int releaseId, int environmentId, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateDeploymentEnvironmentDetailedAsync(releaseId, environmentId, cancellationToken).ConfigureAwait(false);

        return validation.IsValid;
    }

    public async Task<DeploymentEnvironmentValidationResult> ValidateDeploymentEnvironmentDetailedAsync(int releaseId, int environmentId, CancellationToken cancellationToken = default)
    {
        return await ValidateDeploymentEnvironmentDetailedAsync(releaseId, environmentId, new HashSet<int>(), new HashSet<int>(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeploymentEnvironmentValidationResult> ValidateDeploymentEnvironmentDetailedAsync(int releaseId, int environmentId, HashSet<int> specificMachineIds, HashSet<int> excludedMachineIds, CancellationToken cancellationToken = default)
    {
        Log.Information("Validating deployment environment for release {ReleaseId} and environment {EnvironmentId}", releaseId, environmentId);

        var result = new DeploymentEnvironmentValidationResult();
        
        specificMachineIds ??= new HashSet<int>();
        excludedMachineIds ??= new HashSet<int>();

        var release = await _releaseDataProvider
            .GetReleaseByIdAsync(releaseId, cancellationToken)
            .ConfigureAwait(false);

        if (release == null)
        {
            Log.Warning("Release {ReleaseId} not found", releaseId);
            result.Reasons.Add($"Release {releaseId} not found.");
            return result;
        }

        var environment = await _environmentDataProvider.GetEnvironmentByIdAsync(environmentId, cancellationToken).ConfigureAwait(false);

        if (environment == null)
        {
            Log.Warning("Environment {EnvironmentId} not found", environmentId);
            result.Reasons.Add($"Environment {environmentId} not found.");
            return result;
        }

        var environmentIds = new HashSet<int> { environmentId };
        
        var machines = await _machineDataProvider
            .GetMachinesByFilterAsync(environmentIds, new HashSet<string>(), cancellationToken).ConfigureAwait(false);

        machines = ApplyMachineSelection(machines, specificMachineIds, excludedMachineIds);
        
        result.AvailableMachineCount = machines.Count;

        if (!machines.Any())
        {
            Log.Warning("No available machines found in environment {EnvironmentId}", environmentId);

            if (specificMachineIds.Count > 0 || excludedMachineIds.Count > 0)
            {
                result.Reasons.Add($"No available machines found after applying machine selection constraints in environment {environmentId}.");
            }
            else
            {
                result.Reasons.Add($"No available machines found in environment {environmentId}.");
            }
        }

        var lifecycle = await _lifecycleResolver
            .ResolveLifecycleAsync(release.ProjectId, release.ChannelId, cancellationToken).ConfigureAwait(false);

        result.LifecycleId = lifecycle.Id;

        var progression = await _progressionEvaluator
            .EvaluateProgressionAsync(lifecycle.Id, release.ProjectId, cancellationToken).ConfigureAwait(false);

        result.AllowedEnvironmentIds = progression.AllowedEnvironmentIds;

        if (!progression.AllowedEnvironmentIds.Contains(environmentId))
        {
            Log.Warning(
                "Environment {EnvironmentId} is not allowed by lifecycle {LifecycleId} progression",
                environmentId,
                lifecycle.Id);

            var allowedText = progression.AllowedEnvironmentIds.Count == 0
                ? "<none>"
                : string.Join(", ", progression.AllowedEnvironmentIds);

            result.Reasons.Add(
                $"Environment {environmentId} is not allowed by lifecycle {lifecycle.Id} progression. Allowed: {allowedText}.");
        }

        result.IsValid = result.Reasons.Count == 0;

        if (result.IsValid)
            Log.Information("Environment validation passed: found {MachineCount} available machines", machines.Count);

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
