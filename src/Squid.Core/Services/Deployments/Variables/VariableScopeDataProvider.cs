using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Variables;

public interface IVariableScopeDataProvider : IScopedDependency
{
    Task AddVariableScopesAsync(List<VariableScope> scopes, CancellationToken cancellationToken = default);

    Task DeleteVariableScopesByVariableIdAsync(int variableId, CancellationToken cancellationToken = default);

    Task<List<VariableScope>> GetVariableScopesByVariableIdAsync(int variableId, CancellationToken cancellationToken = default);

    Task<List<VariableScope>> GetVariableScopesByVariableIdsAsync(List<int> variableIds, CancellationToken cancellationToken = default);
}

public class VariableScopeDataProvider : IVariableScopeDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public VariableScopeDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddVariableScopesAsync(List<VariableScope> scopes, CancellationToken cancellationToken = default)
    {
        if (scopes.Count == 0) return;

        await _repository.InsertAllAsync(scopes, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteVariableScopesByVariableIdAsync(int variableId, CancellationToken cancellationToken = default)
    {
        var scopes = await _repository.Query<VariableScope>()
            .Where(s => s.VariableId == variableId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (scopes.Count == 0) return;

        await _repository.DeleteAllAsync(scopes, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<VariableScope>> GetVariableScopesByVariableIdAsync(int variableId, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<VariableScope>()
            .Where(s => s.VariableId == variableId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<VariableScope>> GetVariableScopesByVariableIdsAsync(List<int> variableIds, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<VariableScope>()
            .Where(s => variableIds.Contains(s.VariableId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
