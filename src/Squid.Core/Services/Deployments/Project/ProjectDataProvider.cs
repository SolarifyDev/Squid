namespace Squid.Core.Services.Deployments.Project;

public interface IProjectDataProvider : IScopedDependency
{
    Task<Message.Domain.Deployments.Project> GetProjectByIdAsync(int projectId, CancellationToken cancellationToken = default);

    Task AddProjectAsync(Message.Domain.Deployments.Project project, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateProjectAsync(Message.Domain.Deployments.Project project, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteProjectsAsync(List<Message.Domain.Deployments.Project> projects, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<Message.Domain.Deployments.Project>)> GetProjectPagingAsync(int? pageIndex = null, int? pageSize = null, string keyword = null, CancellationToken cancellationToken = default);

    Task<List<Message.Domain.Deployments.Project>> GetProjectsAsync(List<int> ids, CancellationToken cancellationToken = default);
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

    public async Task AddProjectAsync(Message.Domain.Deployments.Project project, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(project, cancellationToken).ConfigureAwait(false);

        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateProjectAsync(Message.Domain.Deployments.Project project, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(project, cancellationToken).ConfigureAwait(false);

        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteProjectsAsync(List<Message.Domain.Deployments.Project> projects, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAllAsync(projects, cancellationToken).ConfigureAwait(false);

        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int count, List<Message.Domain.Deployments.Project>)> GetProjectPagingAsync(int? pageIndex = null, int? pageSize = null, string keyword = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<Message.Domain.Deployments.Project>();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(p => p.Name.Contains(keyword) || p.Slug.Contains(keyword));
        }

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);

        return (count, await query.ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<List<Message.Domain.Deployments.Project>> GetProjectsAsync(List<int> ids, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<Message.Domain.Deployments.Project>()
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
