using Squid.Core.Utils;
using Squid.Core.Services.DeploymentExecution.Exceptions;
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
        var projectGroupId = command.Id > 0 ? command.Id : command.ProjectGroup?.Id ?? 0;
        var projectGroup = await _projectGroupDataProvider.GetByIdAsync(projectGroupId, cancellationToken).ConfigureAwait(false);

        if (projectGroup == null)
            throw new DeploymentEntityNotFoundException("ProjectGroup", projectGroupId);

        var update = command.ProjectGroup;
        if (update == null)
            throw new InvalidOperationException("ProjectGroup update payload is required.");

        var targetSpaceId = update.SpaceId;

        if (targetSpaceId != 0 && projectGroup.SpaceId != targetSpaceId)
            throw new DeploymentEntityNotFoundException("ProjectGroup", projectGroupId, "Project group belongs to a different space.");

        if (!string.IsNullOrWhiteSpace(update.Name))
            projectGroup.Name = update.Name;

        projectGroup.Description = update.Description;

        if (!string.IsNullOrWhiteSpace(update.Slug))
            projectGroup.Slug = update.Slug;

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
            request.SpaceId, request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetProjectGroupsResponse
        {
            Data = new GetProjectGroupsResponseData
            {
                Count = count, ProjectGroups = _mapper.Map<List<ProjectGroupDto>>(data)
            }
        };
    }
}
