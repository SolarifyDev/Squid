using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Services.Deployments.Process;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Utils;
using Squid.Message.Commands.Deployments.Project;
using Squid.Message.Enums;
using Squid.Message.Events.Deployments.Project;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Requests.Deployments.Project;

namespace Squid.Core.Services.Deployments.Project;

public interface IProjectService : IScopedDependency
{
    Task<ProjectCreatedEvent> CreateProjectAsync(CreateProjectCommand command, CancellationToken cancellationToken);

    Task<ProjectUpdatedEvent> UpdateProjectAsync(UpdateProjectCommand command, CancellationToken cancellationToken);

    Task<ProjectDeletedEvent> DeleteProjectsAsync(DeleteProjectsCommand command, CancellationToken cancellationToken);

    Task<GetProjectsResponse> GetProjectsAsync(GetProjectsRequest request, CancellationToken cancellationToken);

    Task<GetProjectResponse> GetProjectByIdAsync(int id, CancellationToken cancellationToken);
}

public class ProjectService : IProjectService
{
    private readonly IMapper _mapper;
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IVariableDataProvider _variableDataProvider;
    private readonly IDeploymentProcessDataProvider _processDataProvider;
    private readonly IChannelDataProvider _channelDataProvider;

    public ProjectService(
        IMapper mapper,
        IProjectDataProvider projectDataProvider,
        IVariableDataProvider variableDataProvider,
        IDeploymentProcessDataProvider processDataProvider,
        IChannelDataProvider channelDataProvider)
    {
        _mapper = mapper;
        _projectDataProvider = projectDataProvider;
        _processDataProvider = processDataProvider;
        _variableDataProvider = variableDataProvider;
        _channelDataProvider = channelDataProvider;
    }

    public async Task<ProjectCreatedEvent> CreateProjectAsync(
        CreateProjectCommand command, CancellationToken cancellationToken)
    {
        var project = BuildProject(command);

        // Commit 1: Project (need project.Id for child entities)
        await _projectDataProvider.AddProjectAsync(project, cancellationToken: cancellationToken).ConfigureAwait(false);

        var process = CreateDeploymentProcess(project);
        var variableSet = CreateVariableSet(project);
        var defaultChannel = CreateDefaultChannel(project);

        // Commit 2: child entities (single SaveChanges via last forceSave: true)
        await _processDataProvider.AddDeploymentProcessAsync(process, forceSave: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _variableDataProvider.AddVariableSetAsync(variableSet, forceSave: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _channelDataProvider.AddChannelAsync(defaultChannel, forceSave: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Commit 3: FK writeback (IDs now populated after flush)
        project.DeploymentProcessId = process.Id;
        project.VariableSetId = variableSet.Id;

        await _projectDataProvider.UpdateProjectAsync(project, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ProjectCreatedEvent
        {
            Data = _mapper.Map<ProjectDto>(project)
        };
    }

    public async Task<ProjectUpdatedEvent> UpdateProjectAsync(UpdateProjectCommand command, CancellationToken cancellationToken)
    {
        var project = _mapper.Map<Persistence.Entities.Deployments.Project>(command.Project);

        project.LastModified = DateTimeOffset.UtcNow;

        if (project.IncludedLibraryVariableSetIds == null)
        {
            project.IncludedLibraryVariableSetIds = string.Empty;
        }

        await _projectDataProvider.UpdateProjectAsync(project, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ProjectUpdatedEvent
        {
            Data = _mapper.Map<ProjectDto>(project)
        };
    }

    public async Task<ProjectDeletedEvent> DeleteProjectsAsync(DeleteProjectsCommand command, CancellationToken cancellationToken)
    {
        var projects = await _projectDataProvider.GetProjectsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        await _projectDataProvider.DeleteProjectsAsync(projects, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ProjectDeletedEvent
        {
            Data = new DeleteProjectsResponseData
            {
                FailIds = command.Ids.Except(projects.Select(p => p.Id)).ToList()
            }
        };
    }

    public async Task<GetProjectsResponse> GetProjectsAsync(GetProjectsRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _projectDataProvider.GetProjectPagingAsync(
            request.PageIndex, request.PageSize, request.Keyword, cancellationToken).ConfigureAwait(false);

        return new GetProjectsResponse
        {
            Data = new GetProjectsResponseData
            {
                Count = count,
                Projects = _mapper.Map<List<ProjectDto>>(data)
            }
        };
    }

    public async Task<GetProjectResponse> GetProjectByIdAsync(int id, CancellationToken cancellationToken)
    {
        var project = await _projectDataProvider.GetProjectByIdAsync(id, cancellationToken).ConfigureAwait(false);

        if (project == null)
        {
            throw new DeploymentEntityNotFoundException("Project", id);
        }

        return new GetProjectResponse
        {
            Data = _mapper.Map<ProjectDto>(project)
        };
    }

    private Persistence.Entities.Deployments.Project BuildProject(CreateProjectCommand command)
    {
        var project = _mapper.Map<Persistence.Entities.Deployments.Project>(command.Project);

        project.LastModified = DateTimeOffset.UtcNow;
        project.IncludedLibraryVariableSetIds ??= string.Empty;
        project.Json ??= string.Empty;
        project.DataVersion ??= Array.Empty<byte>();

        if (string.IsNullOrWhiteSpace(project.Slug))
            project.Slug = SlugGenerator.Generate(project.Name);

        return project;
    }

    private static DeploymentProcess CreateDeploymentProcess(
        Persistence.Entities.Deployments.Project project) => new()
    {
        ProjectId = project.Id,
        Version = 1,
        SpaceId = project.SpaceId,
        LastModified = DateTimeOffset.UtcNow,
        LastModifiedBy = "System"
    };

    private static VariableSet CreateVariableSet(
        Persistence.Entities.Deployments.Project project) => new()
    {
        SpaceId = project.SpaceId,
        OwnerType = VariableSetOwnerType.Project,
        OwnerId = project.Id,
        Version = 1,
        LastModified = DateTimeOffset.UtcNow
    };

    private static Channel CreateDefaultChannel(
        Persistence.Entities.Deployments.Project project) => new()
    {
        Name = "Default",
        ProjectId = project.Id,
        SpaceId = project.SpaceId,
        IsDefault = true,
        Slug = "default"
    };
}
