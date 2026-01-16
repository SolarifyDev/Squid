using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Process;
using Squid.Core.Services.Deployments.Variable;
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

    public ProjectService(
        IMapper mapper,
        IProjectDataProvider projectDataProvider,
        IVariableDataProvider variableDataProvider,
        IDeploymentProcessDataProvider processDataProvider)
    {
        _mapper = mapper;
        _projectDataProvider = projectDataProvider;
        _processDataProvider = processDataProvider;
        _variableDataProvider = variableDataProvider;
    }

    public async Task<ProjectCreatedEvent> CreateProjectAsync(CreateProjectCommand command, CancellationToken cancellationToken)
    {
        var project = _mapper.Map<Persistence.Entities.Deployments.Project>(command.Project);
        project.LastModified = DateTimeOffset.UtcNow;

        if (project.IncludedLibraryVariableSetIds == null)
        {
            project.IncludedLibraryVariableSetIds = string.Empty;
        }

        // 先保存 Project，拿到数据库生成的 Id
        await _projectDataProvider.AddProjectAsync(project, cancellationToken: cancellationToken).ConfigureAwait(false);

        var deploymentProcess = new DeploymentProcess
        {
            Version = 1,
            SpaceId = project.SpaceId,
            LastModified = DateTimeOffset.UtcNow,
            LastModifiedBy = "System",
            ProjectId = project.Id
        };

        await _processDataProvider.AddDeploymentProcessAsync(deploymentProcess, cancellationToken: cancellationToken).ConfigureAwait(false);

        var variableSet = new VariableSet
        {
            SpaceId = project.SpaceId,
            OwnerType = VariableSetOwnerType.Project,
            OwnerId = project.Id,
            Version = 1,
            LastModified = DateTimeOffset.UtcNow
        };
        
        await _variableDataProvider.AddVariableSetAsync(variableSet, cancellationToken: cancellationToken).ConfigureAwait(false);

        project.VariableSetId = variableSet.Id;
        project.DeploymentProcessId = deploymentProcess.Id;

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
            throw new InvalidOperationException($"Project with id {id} not found");
        }

        return new GetProjectResponse
        {
            Data = _mapper.Map<ProjectDto>(project)
        };
    }
}
