using Squid.Message.Enums;

namespace Squid.Core.Services.Deployments.Variable;

public interface IVariableSetDataProvider : IScopedDependency
{
    Task<Message.Domain.Deployments.VariableSet> GetVariableSetByOwnerAsync(int ownerId, VariableSetOwnerType ownerType, CancellationToken cancellationToken = default);

    Task<List<Message.Domain.Deployments.VariableSet>> GetVariableSetsByOwnerIdsAsync(List<int> ownerIds, VariableSetOwnerType ownerType, CancellationToken cancellationToken = default);

    Task<Message.Domain.Deployments.VariableSet> GetVariableSetByIdAsync(int variableSetId, CancellationToken cancellationToken = default);
}

public class VariableSetDataProvider : IVariableSetDataProvider
{
    private readonly IRepository _repository;

    public VariableSetDataProvider(IRepository repository)
    {
        _repository = repository;
    }

    public async Task<Message.Domain.Deployments.VariableSet> GetVariableSetByOwnerAsync(int ownerId, VariableSetOwnerType ownerType, CancellationToken cancellationToken = default)
    {
        return await _repository.QueryNoTracking<Message.Domain.Deployments.VariableSet>(vs => vs.OwnerId == ownerId && vs.OwnerType == ownerType)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Message.Domain.Deployments.VariableSet>> GetVariableSetsByOwnerIdsAsync(List<int> ownerIds, VariableSetOwnerType ownerType, CancellationToken cancellationToken = default)
    {
        return await _repository.QueryNoTracking<Message.Domain.Deployments.VariableSet>(vs => ownerIds.Contains(vs.OwnerId) && vs.OwnerType == ownerType)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Message.Domain.Deployments.VariableSet> GetVariableSetByIdAsync(int variableSetId, CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync<Message.Domain.Deployments.VariableSet>(variableSetId, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
