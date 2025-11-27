namespace Squid.Core.Services.Deployments.Project;

public interface IProjectDataProvider : IScopedDependency
{
    Task<Message.Domain.Deployments.Project> GetProjectByIdAsync(int projectId, CancellationToken cancellationToken = default);

    Task CreateProjectAsync(Message.Domain.Deployments.Project project, bool forceSave = false, CancellationToken cancellationToken = default);
    
    Task CreateProjectGroup(ProjectGroup projectGroup, bool forceSave = false, CancellationToken cancellationToken = default);
}

public class ProjectDataProvider : IProjectDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ProjectDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Message.Domain.Deployments.Project> GetProjectByIdAsync(int projectId, CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync<Message.Domain.Deployments.Project>(projectId, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateProjectAsync(Message.Domain.Deployments.Project project, bool forceSave = false, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(project, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) 
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateProjectGroup(ProjectGroup projectGroup, bool forceSave = false, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(projectGroup, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) 
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
