using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Deployments;

public interface IDeploymentDataProvider : IScopedDependency
{
    Task<Deployment> GetDeploymentByIdAsync(int deploymentId, CancellationToken cancellationToken = default);

    Task<Deployment> GetDeploymentByTaskIdAsync(int taskId, CancellationToken cancellationToken = default);

    Task UpdateDeploymentAsync(Deployment deployment, bool forceSave = true, CancellationToken cancellationToken = default);

    Task AddDeploymentAsync(Deployment deployment, bool forceSave = true, CancellationToken cancellationToken = default);
}

public class DeploymentDataProvider(IRepository repository, IUnitOfWork unitOfWork) : IDeploymentDataProvider
{
    public async Task<Deployment> GetDeploymentByIdAsync(int deploymentId, CancellationToken cancellationToken = default)
    {
        return await repository.GetByIdAsync<Deployment>(deploymentId, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<Deployment> GetDeploymentByTaskIdAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return await repository.QueryNoTracking<Deployment>(d => d.TaskId == taskId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateDeploymentAsync(Deployment deployment, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAsync(deployment, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AddDeploymentAsync(Deployment deployment, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(deployment, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
