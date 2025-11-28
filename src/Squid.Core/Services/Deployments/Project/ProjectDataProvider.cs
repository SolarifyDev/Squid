namespace Squid.Core.Services.Deployments.Project;

public interface IProjectDataProvider : IScopedDependency
{
    Task<Message.Domain.Deployments.Project> GetProjectByIdAsync(int projectId, CancellationToken cancellationToken = default);

    Task<List<Message.Domain.Deployments.Project>> GetProjectsPagingAsync(int spaceId, int? projectGroupId = null, int? projectId = null, int? environmentId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<List<ProjectGroup>> GetProjectGroupsAsync(int spaceId, CancellationToken cancellationToken = default);

    Task CreateProjectAsync(Message.Domain.Deployments.Project project, bool forceSave = false, CancellationToken cancellationToken = default);
    
    Task CreateProjectGroup(ProjectGroup projectGroup, bool forceSave = false, CancellationToken cancellationToken = default);

    Task<List<(ProjectGroup, List<Message.Domain.Deployments.Project>)>> GetProjectGroupWithProjectsPagingAsync(int spaceId, int? projectGroupId = null, int? projectId = null, int? environmentId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);
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

    public async Task<List<Message.Domain.Deployments.Project>> GetProjectsPagingAsync(int spaceId, int? projectGroupId = null, int? projectId = null, int? environmentId = null,
        int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<Message.Domain.Deployments.Project>()
            .Where(x => x.SpaceId == spaceId);

        if (projectGroupId.HasValue)
            query = query.Where(x => x.ProjectGroupId == projectGroupId.Value);

        if (projectId.HasValue)
            query = query.Where(x => x.Id == projectId.Value);

        if (environmentId.HasValue)
            query = query.Where(x => x.EnvironmentId == environmentId.Value);

        if (pageIndex.HasValue && pageSize.HasValue && pageIndex.Value >= 1 && pageSize.Value > 0)
        {
            int skip = (pageIndex.Value - 1) * pageSize.Value;
            query = query.Skip(skip).Take(pageSize.Value);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<ProjectGroup>> GetProjectGroupsAsync(int spaceId, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ProjectGroup>().Where(x => x.SpaceId == spaceId).ToListAsync(cancellationToken).ConfigureAwait(false);
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

    public Task<List<(ProjectGroup, List<Message.Domain.Deployments.Project>)>> GetProjectGroupWithProjectsPagingAsync(int spaceId, int? projectGroupId = null, int? projectId = null,
        int? environmentId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
