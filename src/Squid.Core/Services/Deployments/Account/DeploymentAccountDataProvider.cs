using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;

namespace Squid.Core.Services.Deployments.Account;

public interface IDeploymentAccountDataProvider : IScopedDependency
{
    Task<DeploymentAccount> GetAccountByIdAsync(int accountId, CancellationToken cancellationToken = default);
}

public class DeploymentAccountDataProvider : IDeploymentAccountDataProvider
{
    private readonly IRepository _repository;

    public DeploymentAccountDataProvider(IRepository repository)
    {
        _repository = repository;
    }

    public async Task<DeploymentAccount> GetAccountByIdAsync(int accountId, CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync<DeploymentAccount>(accountId, cancellationToken).ConfigureAwait(false);
    }
}

