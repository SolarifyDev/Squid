using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Account;

public interface IDeploymentAccountDataProvider : IScopedDependency
{
    Task<DeploymentAccount> GetAccountByIdAsync(int accountId, CancellationToken cancellationToken = default);
    Task<DeploymentAccount> AddAccountAsync(DeploymentAccount account, bool forceSave = true, CancellationToken cancellationToken = default);
    Task UpdateAccountAsync(DeploymentAccount account, bool forceSave = true, CancellationToken cancellationToken = default);
    Task DeleteAccountsAsync(List<DeploymentAccount> accounts, bool forceSave = true, CancellationToken cancellationToken = default);
    Task<List<DeploymentAccount>> GetAccountsByIdsAsync(List<int> ids, CancellationToken cancellationToken = default);
    Task<List<DeploymentAccount>> GetAccountsBySpaceIdAsync(int spaceId, CancellationToken cancellationToken = default);
    Task<(int count, List<DeploymentAccount>)> GetAccountPagingAsync(int? spaceId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);
}

public class DeploymentAccountDataProvider(IRepository repository, IUnitOfWork unitOfWork) : IDeploymentAccountDataProvider
{
    public async Task<DeploymentAccount> GetAccountByIdAsync(int accountId, CancellationToken cancellationToken = default)
    {
        return await repository.GetByIdAsync<DeploymentAccount>(accountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeploymentAccount> AddAccountAsync(DeploymentAccount account, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(account, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return account;
    }

    public async Task UpdateAccountAsync(DeploymentAccount account, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAsync(account, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAccountsAsync(List<DeploymentAccount> accounts, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAllAsync(accounts, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DeploymentAccount>> GetAccountsByIdsAsync(List<int> ids, CancellationToken cancellationToken = default)
    {
        return await repository.Query<DeploymentAccount>()
            .Where(a => ids.Contains(a.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DeploymentAccount>> GetAccountsBySpaceIdAsync(int spaceId, CancellationToken cancellationToken = default)
    {
        return await repository.Query<DeploymentAccount>()
            .Where(a => a.SpaceId == spaceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int count, List<DeploymentAccount>)> GetAccountPagingAsync(int? spaceId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = repository.Query<DeploymentAccount>();

        if (spaceId.HasValue)
            query = query.Where(a => a.SpaceId == spaceId.Value);

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);

        return (count, await query.ToListAsync(cancellationToken).ConfigureAwait(false));
    }
}

