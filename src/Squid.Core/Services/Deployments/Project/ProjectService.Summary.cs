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
        var environments = await LoadEnvironmentsAsync(request, cancellationToken).ConfigureAwait(false);
        var items = await LoadDashboardItemsAsync(projects, cancellationToken).ConfigureAwait(false);
        var summaries = BuildGroupSummaries(groups, projects, items);

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

    private async Task<List<DeploymentEnvironment>> LoadEnvironmentsAsync(GetProjectSummariesRequest request, CancellationToken ct)
    {
        if (request.EnvironmentIds is { Count: > 0 })
            return await _environmentDataProvider.GetEnvironmentsByIdsAsync(request.EnvironmentIds, ct).ConfigureAwait(false);

        var (_, environments) = await _environmentDataProvider.GetEnvironmentPagingAsync(cancellationToken: ct).ConfigureAwait(false);

        return environments;
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
            .Select(g => g.OrderByDescending(d => d.Created).First())
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

    private List<ProjectGroupSummaryDto> BuildGroupSummaries(List<Persistence.Entities.Deployments.ProjectGroup> groups, List<Persistence.Entities.Deployments.Project> projects, List<ProjectDashboardItemDto> items)
    {
        var projectsByGroup = projects
            .GroupBy(p => p.ProjectGroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var environmentIdsByGroup = items
            .GroupBy(i => projects.FirstOrDefault(p => p.Id == i.ProjectId)?.ProjectGroupId ?? 0)
            .ToDictionary(g => g.Key, g => g.Select(i => i.EnvironmentId).Distinct().ToList());

        return groups.Select(g =>
        {
            projectsByGroup.TryGetValue(g.Id, out var groupProjects);
            environmentIdsByGroup.TryGetValue(g.Id, out var envIds);

            return new ProjectGroupSummaryDto
            {
                Id = g.Id,
                Name = g.Name,
                Description = g.Description,
                Slug = g.Slug,
                Projects = _mapper.Map<List<ProjectDto>>(groupProjects ?? []),
                EnvironmentIds = envIds ?? []
            };
        }).ToList();
    }
}
