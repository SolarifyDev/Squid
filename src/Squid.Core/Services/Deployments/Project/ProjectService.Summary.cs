using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Requests.Deployments.Project;
using DeploymentEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.Core.Services.Deployments.Project;

public partial class ProjectService
{
    public async Task<GetProjectSummariesResponse> GetProjectSummariesAsync(
        GetProjectSummariesRequest request, CancellationToken cancellationToken)
    {
        var groups = await LoadProjectGroupsAsync(request, cancellationToken).ConfigureAwait(false);
        var projects = await LoadProjectsAsync(request, groups, cancellationToken).ConfigureAwait(false);
        var lifecycleEnvMap = await ResolveLifecycleEnvironmentIdsAsync(projects, cancellationToken).ConfigureAwait(false);
        var environments = await LoadEnvironmentsAsync(request, lifecycleEnvMap, cancellationToken).ConfigureAwait(false);
        var items = await LoadDashboardItemsAsync(projects, cancellationToken).ConfigureAwait(false);
        var summaries = BuildGroupSummaries(groups, projects, lifecycleEnvMap);

        return new GetProjectSummariesResponse
        {
            Data = new GetProjectSummariesResponseData
            {
                Items = items,
                Groups = summaries,
                Environments = environments.Select(e => new ProjectDashboardEnvironmentDto { Id = e.Id, Name = e.Name }).ToList()
            }
        };
    }

    private async Task<List<Persistence.Entities.Deployments.ProjectGroup>> LoadProjectGroupsAsync(GetProjectSummariesRequest request, CancellationToken ct)
    {
        if (request.ProjectGroupIds is { Count: > 0 })
            return await _projectGroupDataProvider.GetProjectGroupsAsync(request.ProjectGroupIds, ct).ConfigureAwait(false);

        var (_, groups) = await _projectGroupDataProvider.GetProjectGroupPagingAsync(ct: ct).ConfigureAwait(false);

        return groups;
    }

    private async Task<List<Persistence.Entities.Deployments.Project>> LoadProjectsAsync(GetProjectSummariesRequest request, List<Persistence.Entities.Deployments.ProjectGroup> groups, CancellationToken ct)
    {
        if (request.ProjectIds is { Count: > 0 })
            return await _projectDataProvider.GetProjectsAsync(request.ProjectIds, ct).ConfigureAwait(false);

        var allProjects = await _projectDataProvider.GetAllProjectsAsync(ct).ConfigureAwait(false);

        var groupIds = groups.Select(g => g.Id).ToHashSet();

        return allProjects.Where(p => groupIds.Contains(p.ProjectGroupId)).ToList();
    }

    private async Task<Dictionary<int, HashSet<int>>> ResolveLifecycleEnvironmentIdsAsync(List<Persistence.Entities.Deployments.Project> projects, CancellationToken ct)
    {
        var lifecycleIds = projects.Select(p => p.LifecycleId).Distinct().ToList();

        if (lifecycleIds.Count == 0) return new();

        var phases = await _lifeCycleDataProvider.GetPhasesByLifecycleIdsAsync(lifecycleIds, ct).ConfigureAwait(false);

        var phaseIds = phases.Select(p => p.Id).ToList();

        var phaseEnvironments = phaseIds.Count > 0
            ? await _lifeCycleDataProvider.GetPhaseEnvironmentsByPhaseIdsAsync(phaseIds, ct).ConfigureAwait(false)
            : new List<LifecyclePhaseEnvironment>();

        var phaseEnvLookup = phaseEnvironments
            .GroupBy(pe => pe.PhaseId)
            .ToDictionary(g => g.Key, g => g.Select(pe => pe.EnvironmentId).ToHashSet());

        var phasesByLifecycle = phases
            .GroupBy(p => p.LifecycleId)
            .ToDictionary(g => g.Key, g => g.ToList());

        HashSet<int> allEnvironmentIds = null;

        var result = new Dictionary<int, HashSet<int>>();

        foreach (var lifecycleId in lifecycleIds)
        {
            if (!phasesByLifecycle.TryGetValue(lifecycleId, out var lifecyclePhases) || lifecyclePhases.Count == 0)
            {
                allEnvironmentIds ??= await LoadAllEnvironmentIdsAsync(ct).ConfigureAwait(false);
                result[lifecycleId] = allEnvironmentIds;
                continue;
            }

            var envIds = new HashSet<int>();
            var explicitEnvIds = new HashSet<int>();

            foreach (var phase in lifecyclePhases)
            {
                if (phaseEnvLookup.TryGetValue(phase.Id, out var phaseEnvIds) && phaseEnvIds.Count > 0)
                {
                    envIds.UnionWith(phaseEnvIds);
                    explicitEnvIds.UnionWith(phaseEnvIds);
                }
            }

            var hasEmptyPhase = lifecyclePhases.Any(p =>
                !phaseEnvLookup.TryGetValue(p.Id, out var ids) || ids.Count == 0);

            if (hasEmptyPhase)
            {
                allEnvironmentIds ??= await LoadAllEnvironmentIdsAsync(ct).ConfigureAwait(false);

                foreach (var envId in allEnvironmentIds)
                {
                    if (!explicitEnvIds.Contains(envId))
                        envIds.Add(envId);
                }
            }

            result[lifecycleId] = envIds;
        }

        return result;
    }

    private async Task<HashSet<int>> LoadAllEnvironmentIdsAsync(CancellationToken ct)
    {
        var (_, environments) = await _environmentDataProvider.GetEnvironmentPagingAsync(cancellationToken: ct).ConfigureAwait(false);

        return environments.Select(e => e.Id).ToHashSet();
    }

    private async Task<List<DeploymentEnvironment>> LoadEnvironmentsAsync(
        GetProjectSummariesRequest request, Dictionary<int, HashSet<int>> lifecycleEnvMap, CancellationToken ct)
    {
        if (request.EnvironmentIds is { Count: > 0 })
            return await _environmentDataProvider.GetEnvironmentsByIdsAsync(request.EnvironmentIds, ct).ConfigureAwait(false);

        var allEnvIds = lifecycleEnvMap.Values
            .SelectMany(ids => ids)
            .Distinct()
            .ToList();

        if (allEnvIds.Count == 0) return new();

        return await _environmentDataProvider.GetEnvironmentsByIdsAsync(allEnvIds, ct).ConfigureAwait(false);
    }

    private async Task<List<ProjectDashboardItemDto>> LoadDashboardItemsAsync(List<Persistence.Entities.Deployments.Project> projects, CancellationToken ct)
    {
        var projectIds = projects.Select(p => p.Id).ToList();

        if (projectIds.Count == 0) return new();

        var deployments = await _repository
            .QueryNoTracking<Deployment>(d => projectIds.Contains(d.ProjectId))
            .ToListAsync(ct).ConfigureAwait(false);

        if (deployments.Count == 0) return new();

        var taskIds = deployments
            .Where(d => d.TaskId.HasValue)
            .Select(d => d.TaskId.Value)
            .Distinct()
            .ToList();

        var releaseIds = deployments
            .Select(d => d.ReleaseId)
            .Distinct()
            .ToList();

        var tasks = await _repository
            .QueryNoTracking<ServerTaskEntity>(t => taskIds.Contains(t.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        var releases = await _repository
            .QueryNoTracking<ReleaseEntity>(r => releaseIds.Contains(r.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        var taskMap = tasks.ToDictionary(t => t.Id);
        var releaseMap = releases.ToDictionary(r => r.Id);

        return deployments
            .GroupBy(d => (d.ProjectId, d.EnvironmentId))
            .Select(g => g.OrderByDescending(d => d.CreatedDate).First())
            .Where(d => d.TaskId.HasValue && taskMap.ContainsKey(d.TaskId.Value) && releaseMap.ContainsKey(d.ReleaseId))
            .Select(d => BuildDashboardItem(d, taskMap[d.TaskId.Value], releaseMap[d.ReleaseId]))
            .ToList();
    }

    private static ProjectDashboardItemDto BuildDashboardItem(Deployment deployment, ServerTaskEntity task, ReleaseEntity release)
    {
        return new ProjectDashboardItemDto
        {
            ProjectId = deployment.ProjectId,
            EnvironmentId = deployment.EnvironmentId,
            ReleaseVersion = release.Version,
            State = task.State,
            CompletedTime = task.CompletedTime,
            HasWarningsOrErrors = task.HasWarningsOrErrors
        };
    }

    private List<ProjectGroupSummaryDto> BuildGroupSummaries(List<Persistence.Entities.Deployments.ProjectGroup> groups, List<Persistence.Entities.Deployments.Project> projects, Dictionary<int, HashSet<int>> lifecycleEnvMap)
    {
        var projectsByGroup = projects
            .GroupBy(p => p.ProjectGroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return groups.Select(g =>
        {
            projectsByGroup.TryGetValue(g.Id, out var groupProjects);

            var envIds = (groupProjects ?? [])
                .SelectMany(p => lifecycleEnvMap.TryGetValue(p.LifecycleId, out var ids) ? ids : [])
                .Distinct()
                .ToList();

            return new ProjectGroupSummaryDto
            {
                Id = g.Id,
                Name = g.Name,
                Description = g.Description,
                Slug = g.Slug,
                Projects = _mapper.Map<List<ProjectDto>>(groupProjects ?? []),
                EnvironmentIds = envIds
            };
        }).ToList();
    }
}
