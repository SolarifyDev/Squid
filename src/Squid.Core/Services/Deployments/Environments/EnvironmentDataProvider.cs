using Squid.Core.Persistence.Db;
using DeploymentEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.Core.Services.Deployments.Environments;

public interface IEnvironmentDataProvider : IScopedDependency
{
    Task AddEnvironmentAsync(DeploymentEnvironment environment, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateEnvironmentAsync(DeploymentEnvironment environment, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteEnvironmentsAsync(List<DeploymentEnvironment> environments, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int Count, List<DeploymentEnvironment>)> GetEnvironmentPagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<List<DeploymentEnvironment>> GetEnvironmentsByIdsAsync(List<int> ids, CancellationToken cancellationToken);

    Task<DeploymentEnvironment> GetEnvironmentByIdAsync(int environmentId, CancellationToken cancellationToken = default);
}

public class EnvironmentDataProvider(IUnitOfWork unitOfWork, IRepository repository) : IEnvironmentDataProvider
{
    public async Task AddEnvironmentAsync(DeploymentEnvironment environment, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(environment, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateEnvironmentAsync(DeploymentEnvironment environment, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAsync(environment, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteEnvironmentsAsync(List<DeploymentEnvironment> environments, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAllAsync(environments, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int Count, List<DeploymentEnvironment>)> GetEnvironmentPagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = repository.Query<DeploymentEnvironment>();

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);

        return (count, await query.ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<List<DeploymentEnvironment>> GetEnvironmentsByIdsAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await repository.Query<DeploymentEnvironment>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeploymentEnvironment> GetEnvironmentByIdAsync(int environmentId, CancellationToken cancellationToken = default)
    {
        return await repository.GetByIdAsync<DeploymentEnvironment>(environmentId, cancellationToken).ConfigureAwait(false);
    }
}
