using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums;

namespace Squid.Core.Services.Deployments.Variable;

public interface IVariableSetDataProvider : IScopedDependency
{
    Task<VariableSet> GetVariableSetByOwnerAsync(int ownerId, VariableSetOwnerType ownerType, CancellationToken cancellationToken = default);

    Task<List<VariableSet>> GetVariableSetsByOwnerIdsAsync(List<int> ownerIds, VariableSetOwnerType ownerType, CancellationToken cancellationToken = default);

    Task<VariableSet> GetVariableSetByIdAsync(int variableSetId, CancellationToken cancellationToken = default);
    
    Task<List<VariableSet>> GetVariableSetsByIdAsync(List<int> variableSetIds, CancellationToken cancellationToken = default);
}

public class VariableSetDataProvider : IVariableSetDataProvider
{
    private readonly IRepository _repository;

    public VariableSetDataProvider(IRepository repository)
    {
        _repository = repository;
    }

    public async Task<VariableSet> GetVariableSetByOwnerAsync(int ownerId, VariableSetOwnerType ownerType, CancellationToken cancellationToken = default)
    {
        return await _repository.QueryNoTracking<VariableSet>(vs => vs.OwnerId == ownerId && vs.OwnerType == ownerType)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<VariableSet>> GetVariableSetsByOwnerIdsAsync(List<int> ownerIds, VariableSetOwnerType ownerType, CancellationToken cancellationToken = default)
    {
        return await _repository.QueryNoTracking<VariableSet>(vs => ownerIds.Contains(vs.OwnerId) && vs.OwnerType == ownerType)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<VariableSet> GetVariableSetByIdAsync(int variableSetId, CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync<VariableSet>(variableSetId, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<VariableSet>> GetVariableSetsByIdAsync(List<int> variableSetIds, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<VariableSet>(x => variableSetIds.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
