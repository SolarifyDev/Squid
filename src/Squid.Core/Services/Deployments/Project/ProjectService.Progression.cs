using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Requests.Deployments.Project;
using DeploymentEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.Core.Services.Deployments.Project;

public partial class ProjectService
{
    private const int ReleasesPerChannel = 3;

    public async Task<GetProjectProgressionResponse> GetProjectProgressionAsync(
        GetProjectProgressionRequest request, CancellationToken cancellationToken)
    {
        var projectId = request.ProjectId;

        var channels = await LoadChannelsByProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var channelEnvMap = await BuildChannelEnvironmentsAsync(projectId, channels, cancellationToken).ConfigureAwait(false);
        var environments = await LoadProgressionEnvironmentsAsync(channelEnvMap, cancellationToken).ConfigureAwait(false);
        var releases = await LoadProgressionReleasesAsync(projectId, channels, cancellationToken).ConfigureAwait(false);
        var deployments = await LoadDeploymentsForReleasesAsync(releases, cancellationToken).ConfigureAwait(false);
        var currentDeploymentIds = MarkCurrentDeployments(deployments);
        var nextDeploymentsByChannel = await EvaluateNextDeploymentsAsync(projectId, channels, cancellationToken).ConfigureAwait(false);
        var releaseProgressions = AssembleReleaseProgressions(channels, releases, deployments, currentDeploymentIds, nextDeploymentsByChannel);

        return new GetProjectProgressionResponse
        {
            Data = new ProjectProgressionDto
            {
                Environments = environments.Select(e => new ProjectDashboardEnvironmentDto { Id = e.Id, Name = e.Name }).ToList(),
                ChannelEnvironments = channelEnvMap,
                Releases = releaseProgressions
            }
        };
    }

    private async Task<List<Channel>> LoadChannelsByProjectAsync(int projectId, CancellationToken ct)
    {
        return await _channelDataProvider.GetChannelsByProjectIdAsync(projectId, ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, List<int>>> BuildChannelEnvironmentsAsync(int projectId, List<Channel> channels, CancellationToken ct)
    {
        var result = new Dictionary<int, List<int>>();

        var lifecycleCache = new Dictionary<int, List<int>>();

        foreach (var channel in channels)
        {
            var lifecycle = await _lifecycleResolver.ResolveLifecycleAsync(projectId, channel.Id, ct).ConfigureAwait(false);

            if (lifecycleCache.TryGetValue(lifecycle.Id, out var cachedEnvIds))
            {
                result[channel.Id] = cachedEnvIds;
                continue;
            }

            var phases = await _lifeCycleDataProvider.GetPhasesByLifecycleIdAsync(lifecycle.Id, ct).ConfigureAwait(false);

            var phaseIds = phases.Select(p => p.Id).ToList();

            var phaseEnvironments = phaseIds.Count > 0
                ? await _lifeCycleDataProvider.GetPhaseEnvironmentsByPhaseIdsAsync(phaseIds, ct).ConfigureAwait(false)
                : new List<LifecyclePhaseEnvironment>();

            var envIds = phaseEnvironments.Select(pe => pe.EnvironmentId).Distinct().ToList();

            lifecycleCache[lifecycle.Id] = envIds;
            result[channel.Id] = envIds;
        }

        return result;
    }

    private async Task<List<DeploymentEnvironment>> LoadProgressionEnvironmentsAsync(Dictionary<int, List<int>> channelEnvMap, CancellationToken ct)
    {
        var allEnvIds = channelEnvMap.Values.SelectMany(ids => ids).Distinct().ToList();

        if (allEnvIds.Count == 0) return new();

        return await _environmentDataProvider.GetEnvironmentsByIdsAsync(allEnvIds, ct).ConfigureAwait(false);
    }

    private async Task<List<ReleaseEntity>> LoadProgressionReleasesAsync(int projectId, List<Channel> channels, CancellationToken ct)
    {
        var topReleaseIds = new HashSet<int>();

        foreach (var channel in channels)
        {
            var channelReleases = await _repository
                .QueryNoTracking<ReleaseEntity>(r => r.ProjectId == projectId && r.ChannelId == channel.Id)
                .OrderByDescending(r => r.CreatedDate)
                .Take(ReleasesPerChannel)
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var r in channelReleases)
                topReleaseIds.Add(r.Id);
        }

        var deployedReleaseIds = await LoadCurrentlyDeployedReleaseIdsAsync(projectId, ct).ConfigureAwait(false);

        topReleaseIds.UnionWith(deployedReleaseIds);

        if (topReleaseIds.Count == 0) return new();

        var allReleaseIds = topReleaseIds.ToList();

        return await _repository
            .QueryNoTracking<ReleaseEntity>(r => allReleaseIds.Contains(r.Id))
            .OrderByDescending(r => r.CreatedDate)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private async Task<HashSet<int>> LoadCurrentlyDeployedReleaseIdsAsync(int projectId, CancellationToken ct)
    {
        var completions = await _deploymentCompletionDataProvider.GetLatestSuccessfulCompletionsAsync(projectId, ct).ConfigureAwait(false);

        if (completions.Count == 0) return new();

        var deploymentIds = completions.Select(c => c.DeploymentId).Distinct().ToList();

        var deployments = await _repository
            .QueryNoTracking<Deployment>(d => deploymentIds.Contains(d.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        return deployments.Select(d => d.ReleaseId).ToHashSet();
    }

    private async Task<List<Deployment>> LoadDeploymentsForReleasesAsync(List<ReleaseEntity> releases, CancellationToken ct)
    {
        var releaseIds = releases.Select(r => r.Id).ToList();

        if (releaseIds.Count == 0) return new();

        return await _repository
            .QueryNoTracking<Deployment>(d => releaseIds.Contains(d.ReleaseId))
            .OrderByDescending(d => d.CreatedDate)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, List<int>>> EvaluateNextDeploymentsAsync(int projectId, List<Channel> channels, CancellationToken ct)
    {
        var result = new Dictionary<int, List<int>>();

        foreach (var channel in channels)
        {
            var lifecycle = await _lifecycleResolver.ResolveLifecycleAsync(projectId, channel.Id, ct).ConfigureAwait(false);
            var progression = await _progressionEvaluator.EvaluateProgressionAsync(lifecycle.Id, projectId, ct).ConfigureAwait(false);

            result[channel.Id] = progression.AllowedEnvironmentIds;
        }

        return result;
    }

    private HashSet<int> MarkCurrentDeployments(List<Deployment> deployments)
    {
        return deployments
            .GroupBy(d => d.EnvironmentId)
            .Select(g => g.OrderByDescending(d => d.CreatedDate).First())
            .Select(d => d.Id)
            .ToHashSet();
    }

    private List<ReleaseProgressionDto> AssembleReleaseProgressions(List<Channel> channels, List<ReleaseEntity> releases, List<Deployment> deployments, HashSet<int> currentDeploymentIds, Dictionary<int, List<int>> nextDeploymentsByChannel)
    {
        var channelMap = channels.ToDictionary(c => c.Id);

        var taskIds = deployments.Where(d => d.TaskId.HasValue).Select(d => d.TaskId.Value).Distinct().ToList();
        var taskMap = new Dictionary<int, ServerTaskEntity>();

        if (taskIds.Count > 0)
        {
            var tasks = _repository.QueryNoTracking<ServerTaskEntity>(t => taskIds.Contains(t.Id)).ToList();

            taskMap = tasks.ToDictionary(t => t.Id);
        }

        var releaseMap = releases.ToDictionary(r => r.Id);
        var deploymentsByRelease = deployments.GroupBy(d => d.ReleaseId).ToDictionary(g => g.Key, g => g.ToList());

        return releases.Select(release =>
        {
            channelMap.TryGetValue(release.ChannelId, out var channel);

            var releaseDeployments = deploymentsByRelease.GetValueOrDefault(release.Id) ?? new List<Deployment>();

            var deploymentsByEnv = releaseDeployments
                .GroupBy(d => d.EnvironmentId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(d => d.CreatedDate)
                        .Select(d => BuildDeploymentDto(d, releaseMap, taskMap, currentDeploymentIds))
                        .ToList());

            nextDeploymentsByChannel.TryGetValue(release.ChannelId, out var nextEnvIds);

            return new ReleaseProgressionDto
            {
                Release = new ReleaseProgressionReleaseDto
                {
                    Id = release.Id,
                    Version = release.Version,
                    CreatedDate = release.CreatedDate,
                    ChannelId = release.ChannelId
                },
                Channel = channel != null
                    ? new ReleaseProgressionChannelDto { Id = channel.Id, Name = channel.Name }
                    : null,
                Deployments = deploymentsByEnv,
                NextDeployments = nextEnvIds ?? new List<int>()
            };
        }).ToList();
    }

    private static ReleaseProgressionDeploymentDto BuildDeploymentDto(Deployment deployment, Dictionary<int, ReleaseEntity> releaseMap, Dictionary<int, ServerTaskEntity> taskMap, HashSet<int> currentDeploymentIds)
    {
        releaseMap.TryGetValue(deployment.ReleaseId, out var release);

        ServerTaskEntity task = null;

        if (deployment.TaskId.HasValue)
            taskMap.TryGetValue(deployment.TaskId.Value, out task);

        return new ReleaseProgressionDeploymentDto
        {
            DeploymentId = deployment.Id,
            State = task?.State ?? "Unknown",
            ReleaseVersion = release?.Version ?? "",
            CreatedDate = deployment.CreatedDate,
            CompletedTime = task?.CompletedTime,
            HasWarningsOrErrors = task?.HasWarningsOrErrors ?? false,
            IsCurrent = currentDeploymentIds.Contains(deployment.Id)
        };
    }
}
