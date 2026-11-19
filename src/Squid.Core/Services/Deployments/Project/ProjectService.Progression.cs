using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Requests.Deployments.Project;
using DeploymentEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.Core.Services.Deployments.Project;

public partial class ProjectService
{
    public async Task<GetProjectProgressionResponse> GetProjectProgressionAsync(GetProjectProgressionRequest request, CancellationToken cancellationToken)
    {
        var projectId = request.ProjectId;
        var releaseHistoryCount = ResolveReleaseHistoryCount(request.ReleaseHistoryCount);

        var channels = await LoadChannelsByProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var channelEnvMap = await BuildChannelEnvironmentsAsync(projectId, channels, cancellationToken).ConfigureAwait(false);
        var environments = await LoadProgressionEnvironmentsAsync(channelEnvMap, cancellationToken).ConfigureAwait(false);
        var currentDeployments = await LoadCurrentDeploymentsAsync(projectId, channels, cancellationToken).ConfigureAwait(false);
        var previousSuccessfulReleaseIds = await LoadPreviousSuccessfulReleaseIdsAsync(projectId, channels, currentDeployments, cancellationToken).ConfigureAwait(false);
        var recentReleaseIds = await LoadRecentReleaseIdsAsync(projectId, channels, releaseHistoryCount, cancellationToken).ConfigureAwait(false);
        var releaseIds = SelectDashboardReleaseIds(recentReleaseIds, currentDeployments.Select(d => d.ReleaseId).ToList(), previousSuccessfulReleaseIds);
        var releases = await LoadProgressionReleasesAsync(projectId, channels, releaseIds, cancellationToken).ConfigureAwait(false);
        var deployments = await LoadDeploymentsForReleasesAsync(releases, cancellationToken).ConfigureAwait(false);
        var currentDeploymentIds = currentDeployments.Select(d => d.Id).ToHashSet();
        var nextDeploymentsByChannel = await EvaluateNextDeploymentsAsync(projectId, channels, cancellationToken).ConfigureAwait(false);
        var releaseProgressions = AssembleReleaseProgressions(channels, releases, deployments, currentDeploymentIds, nextDeploymentsByChannel);
        var channelReleaseCounts = await CountReleasesPerChannelAsync(projectId, channels, cancellationToken).ConfigureAwait(false);

        return new GetProjectProgressionResponse
        {
            Data = new ProjectProgressionDto
            {
                Environments = environments.Select(e => new ProjectDashboardEnvironmentDto { Id = e.Id, Name = e.Name }).ToList(),
                ChannelEnvironments = channelEnvMap,
                Releases = releaseProgressions,
                ChannelReleaseCounts = channelReleaseCounts
            }
        };
    }

    internal static int ResolveReleaseHistoryCount(int? requested)
    {
        if (requested is null or <= 0) return GetProjectProgressionRequest.DefaultReleaseHistoryCount;

        return Math.Min(requested.Value, GetProjectProgressionRequest.MaxReleaseHistoryCount);
    }

    internal static HashSet<int> SelectDashboardReleaseIds(List<int> recentReleaseIds, List<int> currentReleaseIds, List<int> previousSuccessfulReleaseIds)
    {
        return recentReleaseIds.Concat(currentReleaseIds).Concat(previousSuccessfulReleaseIds).ToHashSet();
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

    private sealed record DashboardDeployment(int Id, int ReleaseId);

    // Deployment rows outlive their release (no FK; deleting a release or channel leaves them behind).
    // Only deployments of releases the dashboard can still show may be "current" or "previous successful",
    // otherwise an orphan steals the slot and the environment ends up with no current marker at all.
    private IQueryable<Deployment> QueryDeploymentsOfLiveReleases(int projectId, List<Channel> channels)
    {
        var channelIds = channels.Select(channel => channel.Id).ToList();

        return from deployment in _repository.QueryNoTracking<Deployment>(d => d.ProjectId == projectId)
            join release in _repository.QueryNoTracking<ReleaseEntity>(r => r.ProjectId == projectId && channelIds.Contains(r.ChannelId)) on deployment.ReleaseId equals release.Id
            select deployment;
    }

    // Newest deployment per environment across ALL of the project's live deployments (DISTINCT-ON style
    // ROW_NUMBER in the database), so IsCurrent stays correct however few releases the window loads.
    private async Task<List<DashboardDeployment>> LoadCurrentDeploymentsAsync(int projectId, List<Channel> channels, CancellationToken ct)
    {
        var rows = await QueryDeploymentsOfLiveReleases(projectId, channels)
            .GroupBy(d => d.EnvironmentId)
            .Select(g => g.OrderByDescending(d => d.CreatedDate).ThenByDescending(d => d.Id).Select(d => new { d.Id, d.ReleaseId }).First())
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(r => new DashboardDeployment(r.Id, r.ReleaseId)).ToList();
    }

    // Octopus' dashboard.Get(includePreviousSuccessful: true): per environment, the newest Success
    // deployment that is not the current one — i.e. what is still running when the latest deploy failed.
    private async Task<List<int>> LoadPreviousSuccessfulReleaseIdsAsync(int projectId, List<Channel> channels, List<DashboardDeployment> currentDeployments, CancellationToken ct)
    {
        var currentIds = currentDeployments.Select(d => d.Id).ToList();

        var successfulDeployments =
            from deployment in QueryDeploymentsOfLiveReleases(projectId, channels).Where(d => !currentIds.Contains(d.Id))
            join task in _repository.QueryNoTracking<ServerTaskEntity>(t => t.State == TaskState.Success) on deployment.TaskId equals (int?)task.Id
            select deployment;

        return await successfulDeployments
            .GroupBy(d => d.EnvironmentId)
            .Select(g => g.OrderByDescending(d => d.CreatedDate).ThenByDescending(d => d.Id).Select(d => d.ReleaseId).First())
            .ToListAsync(ct).ConfigureAwait(false);
    }

    // One LIMIT-n query per channel: each is an index range scan on (project_id, channel_id, created_date DESC),
    // and a project has a handful of channels (EF cannot translate a per-group Take into a single query).
    private async Task<List<int>> LoadRecentReleaseIdsAsync(int projectId, List<Channel> channels, int releaseHistoryCount, CancellationToken ct)
    {
        var result = new List<int>();

        foreach (var channel in channels)
            result.AddRange(await LoadRecentReleaseIdsForChannelAsync(projectId, channel.Id, releaseHistoryCount, ct).ConfigureAwait(false));

        return result;
    }

    private async Task<List<int>> LoadRecentReleaseIdsForChannelAsync(int projectId, int channelId, int releaseHistoryCount, CancellationToken ct)
    {
        return await _repository
            .QueryNoTracking<ReleaseEntity>(r => r.ProjectId == projectId && r.ChannelId == channelId)
            .OrderByDescending(r => r.CreatedDate).ThenByDescending(r => r.Id)
            .Take(releaseHistoryCount)
            .Select(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private async Task<List<ReleaseEntity>> LoadProgressionReleasesAsync(int projectId, List<Channel> channels, HashSet<int> releaseIds, CancellationToken ct)
    {
        var channelIds = channels.Select(channel => channel.Id).ToList();
        var ids = releaseIds.ToList();

        if (channelIds.Count == 0 || ids.Count == 0) return new();

        return await _repository
            .QueryNoTracking<ReleaseEntity>(r => r.ProjectId == projectId && channelIds.Contains(r.ChannelId) && ids.Contains(r.Id))
            .OrderByDescending(r => r.CreatedDate).ThenByDescending(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, int>> CountReleasesPerChannelAsync(int projectId, List<Channel> channels, CancellationToken ct)
    {
        var channelIds = channels.Select(channel => channel.Id).ToList();

        var counts = await _repository
            .QueryNoTracking<ReleaseEntity>(r => r.ProjectId == projectId && channelIds.Contains(r.ChannelId))
            .GroupBy(r => r.ChannelId)
            .Select(g => new { ChannelId = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        return channelIds.ToDictionary(id => id, id => counts.FirstOrDefault(c => c.ChannelId == id)?.Count ?? 0);
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
