using Squid.Core.Utils;
using Squid.Message.Commands.Deployments.ProjectGroup;
using Squid.Message.Events.Deployments.ProjectGroup;
using Squid.Message.Models.Deployments.ProjectGroup;
using Squid.Message.Requests.Deployments.ProjectGroup;

namespace Squid.Core.Services.Deployments.ProjectGroup;

public interface IProjectGroupService : IScopedDependency
{
    Task<ProjectGroupCreatedEvent> CreateProjectGroupAsync(CreateProjectGroupCommand command, CancellationToken cancellationToken);

    Task<ProjectGroupUpdatedEvent> UpdateProjectGroupAsync(UpdateProjectGroupCommand command, CancellationToken cancellationToken);

    Task<ProjectGroupDeletedEvent> DeleteProjectGroupsAsync(DeleteProjectGroupsCommand command, CancellationToken cancellationToken);

    Task<GetProjectGroupsResponse> GetProjectGroupsAsync(GetProjectGroupsRequest request, CancellationToken cancellationToken);
}

public class ProjectGroupService : IProjectGroupService
{
    private readonly IMapper _mapper;
    private readonly IProjectGroupDataProvider _projectGroupDataProvider;

    public ProjectGroupService(IMapper mapper, IProjectGroupDataProvider projectGroupDataProvider)
    {
        _mapper = mapper;
        _projectGroupDataProvider = projectGroupDataProvider;
    }

    public async Task<ProjectGroupCreatedEvent> CreateProjectGroupAsync(
        CreateProjectGroupCommand command, CancellationToken cancellationToken)
    {
        var projectGroup = _mapper.Map<Persistence.Entities.Deployments.ProjectGroup>(command.ProjectGroup);

        if (string.IsNullOrWhiteSpace(projectGroup.Slug))
            projectGroup.Slug = SlugGenerator.Generate(projectGroup.Name);

        await _projectGroupDataProvider.AddAsync(projectGroup, ct: cancellationToken).ConfigureAwait(false);

        return new ProjectGroupCreatedEvent
        {
            Data = _mapper.Map<ProjectGroupDto>(projectGroup)
        };
    }

    public async Task<ProjectGroupUpdatedEvent> UpdateProjectGroupAsync(
        UpdateProjectGroupCommand command, CancellationToken cancellationToken)
    {
        var projectGroup = _mapper.Map<Persistence.Entities.Deployments.ProjectGroup>(command.ProjectGroup);

        await _projectGroupDataProvider.UpdateAsync(projectGroup, ct: cancellationToken).ConfigureAwait(false);

        return new ProjectGroupUpdatedEvent
        {
            Data = _mapper.Map<ProjectGroupDto>(projectGroup)
        };
    }

    public async Task<ProjectGroupDeletedEvent> DeleteProjectGroupsAsync(
        DeleteProjectGroupsCommand command, CancellationToken cancellationToken)
    {
        var projectGroups = await _projectGroupDataProvider.GetProjectGroupsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        await _projectGroupDataProvider.DeleteAsync(projectGroups, ct: cancellationToken).ConfigureAwait(false);

        return new ProjectGroupDeletedEvent
        {
            Data = new DeleteProjectGroupsResponseData
            {
                FailIds = command.Ids.Except(projectGroups.Select(pg => pg.Id)).ToList()
            }
        };
    }

    public async Task<GetProjectGroupsResponse> GetProjectGroupsAsync(
        GetProjectGroupsRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _projectGroupDataProvider.GetProjectGroupPagingAsync(
            request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetProjectGroupsResponse
        {
            Data = new GetProjectGroupsResponseData
            {
                Count = count, ProjectGroups = _mapper.Map<List<ProjectGroupDto>>(data)
            }
        };
    }
}
