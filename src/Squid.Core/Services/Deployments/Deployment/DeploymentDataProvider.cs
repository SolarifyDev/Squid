using Squid.Core.Persistence.Db;

namespace Squid.Core.Services.Deployments.Deployment;

public interface IDeploymentDataProvider : IScopedDependency
{
    Task<Persistence.Entities.Deployments.Deployment> GetDeploymentByIdAsync(int deploymentId, CancellationToken cancellationToken = default);

    Task<Persistence.Entities.Deployments.Deployment> GetDeploymentByTaskIdAsync(int taskId, CancellationToken cancellationToken = default);

    Task UpdateDeploymentAsync(Persistence.Entities.Deployments.Deployment deployment, bool forceSave = true, CancellationToken cancellationToken = default);

    Task AddDeploymentAsync(Persistence.Entities.Deployments.Deployment deployment, bool forceSave = true, CancellationToken cancellationToken = default);
}

public class DeploymentDataProvider : IDeploymentDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public DeploymentDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Persistence.Entities.Deployments.Deployment> GetDeploymentByIdAsync(int deploymentId, CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync<Persistence.Entities.Deployments.Deployment>(deploymentId, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<Persistence.Entities.Deployments.Deployment> GetDeploymentByTaskIdAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return await _repository.QueryNoTracking<Persistence.Entities.Deployments.Deployment>(d => d.TaskId == taskId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateDeploymentAsync(Persistence.Entities.Deployments.Deployment deployment, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(deployment, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AddDeploymentAsync(Persistence.Entities.Deployments.Deployment deployment, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(deployment, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
