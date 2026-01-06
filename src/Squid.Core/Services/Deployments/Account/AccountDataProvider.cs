using Squid.Core.Persistence.Data;
using Squid.Core.Persistence.Data.Domain.Deployments;
using Squid.Core.Services.Common;

namespace Squid.Core.Services.Deployments.Account;

public interface IAccountDataProvider : IScopedDependency
{
    Task<DeploymentAccount> GetAccountByIdAsync(int accountId, CancellationToken cancellationToken = default);
}

public class AccountDataProvider : IAccountDataProvider
{
    private readonly IRepository _repository;

    public AccountDataProvider(IRepository repository)
    {
        _repository = repository;
    }

    public async Task<DeploymentAccount> GetAccountByIdAsync(int accountId, CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync<DeploymentAccount>(accountId, cancellationToken).ConfigureAwait(false);
    }
}

