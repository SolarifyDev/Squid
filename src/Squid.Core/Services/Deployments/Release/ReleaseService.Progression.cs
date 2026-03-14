using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Requests.Deployments.Release;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.Core.Services.Deployments.Release;

public partial class ReleaseService
{
    public async Task<GetReleaseProgressionResponse> GetReleaseProgressionAsync(
        GetReleaseProgressionRequest request, CancellationToken cancellationToken = default)
    {
        var release = await LoadReleaseAsync(request.ReleaseId, cancellationToken).ConfigureAwait(false);
        var lifecycle = await ResolveLifecycleForReleaseAsync(release, cancellationToken).ConfigureAwait(false);
        var progression = await _progressionEvaluator.EvaluateProgressionForReleaseAsync(lifecycle.Id, release.Id, cancellationToken).ConfigureAwait(false);
        var environmentNames = await LoadEnvironmentNamesAsync(progression, cancellationToken).ConfigureAwait(false);
        var latestDeployments = await LoadLatestDeploymentsForReleaseAsync(release.Id, progression, cancellationToken).ConfigureAwait(false);
        var dto = AssembleProgressionDto(release, lifecycle, progression, environmentNames, latestDeployments);

        return new GetReleaseProgressionResponse { Data = dto };
    }

    private async Task<Persistence.Entities.Deployments.Release> LoadReleaseAsync(int releaseId, CancellationToken ct)
    {
        var release = await _releaseDataProvider.GetReleaseByIdAsync(releaseId, ct).ConfigureAwait(false);

        if (release == null)
            throw new InvalidOperationException($"Release {releaseId} not found");

        return release;
    }

    private async Task<Lifecycle> ResolveLifecycleForReleaseAsync(Persistence.Entities.Deployments.Release release, CancellationToken ct)
    {
        return await _lifecycleResolver.ResolveLifecycleAsync(release.ProjectId, release.ChannelId, ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, string>> LoadEnvironmentNamesAsync(PhaseProgressionResult progression, CancellationToken ct)
    {
        var allEnvIds = progression.Phases
            .SelectMany(p => p.AutomaticEnvironmentIds.Concat(p.OptionalEnvironmentIds))
            .Distinct()
            .ToList();

        if (allEnvIds.Count == 0) return new();

        var environments = await _environmentDataProvider.GetEnvironmentsByIdsAsync(allEnvIds, ct).ConfigureAwait(false);

        return environments.ToDictionary(e => e.Id, e => e.Name);
    }

    private async Task<Dictionary<int, (int DeploymentId, string State, DateTimeOffset CreatedDate, DateTimeOffset? CompletedTime)>> LoadLatestDeploymentsForReleaseAsync(
        int releaseId, PhaseProgressionResult progression, CancellationToken ct)
    {
        var allEnvIds = progression.Phases
            .SelectMany(p => p.AutomaticEnvironmentIds.Concat(p.OptionalEnvironmentIds))
            .Distinct()
            .ToList();

        if (allEnvIds.Count == 0) return new();

        var deployments = await _repository
            .QueryNoTracking<Deployment>(d => d.ReleaseId == releaseId && allEnvIds.Contains(d.EnvironmentId))
            .OrderByDescending(d => d.CreatedDate)
            .ToListAsync(ct).ConfigureAwait(false);

        var taskIds = deployments.Where(d => d.TaskId.HasValue).Select(d => d.TaskId.Value).Distinct().ToList();
        var taskMap = new Dictionary<int, ServerTaskEntity>();

        if (taskIds.Count > 0)
        {
            var tasks = await _repository.QueryNoTracking<ServerTaskEntity>(t => taskIds.Contains(t.Id))
                .ToListAsync(ct).ConfigureAwait(false);

            taskMap = tasks.ToDictionary(t => t.Id);
        }

        var result = new Dictionary<int, (int DeploymentId, string State, DateTimeOffset CreatedDate, DateTimeOffset? CompletedTime)>();

        foreach (var group in deployments.GroupBy(d => d.EnvironmentId))
        {
            var latest = group.First();

            ServerTaskEntity task = null;
            if (latest.TaskId.HasValue)
                taskMap.TryGetValue(latest.TaskId.Value, out task);

            result[group.Key] = (latest.Id, task?.State ?? "Unknown", latest.CreatedDate, task?.CompletedTime);
        }

        return result;
    }

    internal static ReleaseLifecycleProgressionDto AssembleProgressionDto(
        Persistence.Entities.Deployments.Release release, Lifecycle lifecycle, PhaseProgressionResult progression,
        Dictionary<int, string> environmentNames,
        Dictionary<int, (int DeploymentId, string State, DateTimeOffset CreatedDate, DateTimeOffset? CompletedTime)> latestDeployments)
    {
        var allowedSet = progression.AllowedEnvironmentIds.ToHashSet();

        var phases = progression.Phases.Select((phase, index) =>
        {
            var progress = DeterminePhaseProgress(phase, index, progression.CurrentPhaseIndex);

            var environments = phase.AutomaticEnvironmentIds
                .Select(envId => BuildEnvironmentDto(envId, true, allowedSet, environmentNames, latestDeployments))
                .Concat(phase.OptionalEnvironmentIds
                    .Select(envId => BuildEnvironmentDto(envId, false, allowedSet, environmentNames, latestDeployments)))
                .ToList();

            return new ReleasePhaseProgressionDto
            {
                PhaseId = phase.PhaseId,
                PhaseName = phase.PhaseName,
                SortOrder = phase.SortOrder,
                IsComplete = phase.IsComplete,
                IsOptional = phase.IsOptional,
                Progress = progress,
                Environments = environments
            };
        }).ToList();

        return new ReleaseLifecycleProgressionDto
        {
            ReleaseId = release.Id,
            ReleaseVersion = release.Version,
            LifecycleId = lifecycle.Id,
            LifecycleName = lifecycle.Name,
            Phases = phases
        };
    }

    private static string DeterminePhaseProgress(PhaseStatus phase, int index, int currentPhaseIndex)
    {
        if (phase.IsComplete) return "Complete";
        if (index == currentPhaseIndex) return "Current";

        return "Pending";
    }

    private static ReleasePhaseEnvironmentDto BuildEnvironmentDto(
        int envId, bool isAutomatic, HashSet<int> allowedSet,
        Dictionary<int, string> environmentNames,
        Dictionary<int, (int DeploymentId, string State, DateTimeOffset CreatedDate, DateTimeOffset? CompletedTime)> latestDeployments)
    {
        environmentNames.TryGetValue(envId, out var envName);
        latestDeployments.TryGetValue(envId, out var deploymentInfo);

        ReleaseEnvironmentDeploymentDto deployment = null;

        if (deploymentInfo.DeploymentId > 0)
        {
            deployment = new ReleaseEnvironmentDeploymentDto
            {
                DeploymentId = deploymentInfo.DeploymentId,
                State = deploymentInfo.State,
                CreatedDate = deploymentInfo.CreatedDate,
                CompletedTime = deploymentInfo.CompletedTime
            };
        }

        return new ReleasePhaseEnvironmentDto
        {
            EnvironmentId = envId,
            EnvironmentName = envName ?? string.Empty,
            IsAutomatic = isAutomatic,
            CanDeploy = allowedSet.Contains(envId),
            Deployment = deployment
        };
    }
}
