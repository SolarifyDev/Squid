namespace Squid.Core.Services.Deployments.Process;

public interface IDeploymentProcessDataProvider : IScopedDependency
{
    Task AddDeploymentProcessAsync(DeploymentProcess process, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task UpdateDeploymentProcessAsync(DeploymentProcess process, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task DeleteDeploymentProcessAsync(DeploymentProcess process, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task<DeploymentProcess> GetDeploymentProcessByIdAsync(int id, CancellationToken cancellationToken = default);
    
    Task<DeploymentProcess> GetDeploymentProcessByProjectIdAndVersionAsync(int projectId, int version, CancellationToken cancellationToken = default);
    
    Task<(int count, List<DeploymentProcess>)> GetDeploymentProcessPagingAsync(int? projectId = null, int? spaceId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);
    
    Task<int> GetNextVersionAsync(int projectId, CancellationToken cancellationToken = default);
}

public class DeploymentProcessDataProvider : IDeploymentProcessDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public DeploymentProcessDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddDeploymentProcessAsync(DeploymentProcess process, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(process, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateDeploymentProcessAsync(DeploymentProcess process, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        process.LastModified = DateTimeOffset.UtcNow;
        await _repository.UpdateAsync(process, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteDeploymentProcessAsync(DeploymentProcess process, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAsync(process, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<DeploymentProcess> GetDeploymentProcessByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<DeploymentProcess>(x => x.Id == id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeploymentProcess> GetDeploymentProcessByProjectIdAndVersionAsync(int projectId, int version, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<DeploymentProcess>()
            .FirstOrDefaultAsync(p => p.ProjectId == projectId && p.Version == version, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(int count, List<DeploymentProcess>)> GetDeploymentProcessPagingAsync(int? projectId = null, int? spaceId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<DeploymentProcess>();

        if (projectId.HasValue)
        {
            query = query.Where(p => p.ProjectId == projectId.Value);
        }

        if (spaceId.HasValue)
        {
            query = query.Where(p => p.SpaceId == spaceId.Value);
        }

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
        {
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);
        }

        var results = await query.OrderByDescending(p => p.Version)
            .ThenByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return (count, results);
    }

    public async Task<int> GetNextVersionAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var maxVersion = await _repository.Query<DeploymentProcess>()
            .Where(p => p.ProjectId == projectId)
            .MaxAsync(p => (int?)p.Version, cancellationToken).ConfigureAwait(false);

        return (maxVersion ?? 0) + 1;
    }
}
