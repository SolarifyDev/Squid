using Squid.Message.Commands.Deployments.Project;
using Squid.Message.Events.Deployments.Project;

namespace Squid.Core.Services.Deployments.Project;

public interface IProjectService : IScopedDependency
{
    Task<ProjectCreatedEvent> CreateProjectAsync(CreateProjectCommand createProjectCommand, CancellationToken cancellationToken);

    Task<ProjectGroupCreatedEvent> CreateProjectGroupAsync(CreateProjectGroupCommand createProjectGroupCommand, CancellationToken cancellationToken);
}

public class ProjectService : IProjectService
{
    private readonly IMapper _mapper;
    private readonly IProjectDataProvider _projectDataProvider;
    
    public ProjectService(IMapper mapper, IProjectDataProvider projectDataProvider)
    {
        _mapper = mapper;
        _projectDataProvider = projectDataProvider;
    }

    public Task<ProjectCreatedEvent> CreateProjectAsync(CreateProjectCommand createProjectCommand, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<ProjectGroupCreatedEvent> CreateProjectGroupAsync(CreateProjectGroupCommand createProjectGroupCommand, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}