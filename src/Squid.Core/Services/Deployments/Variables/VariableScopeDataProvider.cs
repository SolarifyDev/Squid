using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Variables;

public interface IVariableScopeDataProvider : IScopedDependency
{
    Task AddVariableScopesAsync(List<VariableScope> scopes, CancellationToken cancellationToken = default);
    
    Task UpdateVariableScopesAsync(int variableId, List<VariableScope> scopes, CancellationToken cancellationToken = default);
    
    Task DeleteVariableScopesByVariableIdAsync(int variableId, CancellationToken cancellationToken = default);
    
    Task<List<VariableScope>> GetVariableScopesByVariableIdAsync(int variableId, CancellationToken cancellationToken = default);
    
    Task<VariableScope> GetVariableScopeByIdAsync(int id, CancellationToken cancellationToken = default);
    
    Task<(int count, List<VariableScope>)> GetVariableScopePagingAsync(int? variableId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);
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
        foreach (var scope in scopes)
        {
            await _repository.InsertAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateVariableScopesAsync(int variableId, List<VariableScope> scopes, CancellationToken cancellationToken = default)
    {
        await DeleteVariableScopesByVariableIdAsync(variableId, cancellationToken).ConfigureAwait(false);

        foreach (var scope in scopes)
        {
            scope.VariableId = variableId;
        }
        await AddVariableScopesAsync(scopes, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteVariableScopesByVariableIdAsync(int variableId, CancellationToken cancellationToken = default)
    {
        var scopes = await _repository.Query<VariableScope>()
            .Where(s => s.VariableId == variableId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var scope in scopes)
        {
            await _repository.DeleteAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<VariableScope>> GetVariableScopesByVariableIdAsync(int variableId, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<VariableScope>()
            .Where(s => s.VariableId == variableId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<VariableScope> GetVariableScopeByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<VariableScope>(x => x.Id == id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int count, List<VariableScope>)> GetVariableScopePagingAsync(int? variableId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<VariableScope>();

        if (variableId.HasValue)
        {
            query = query.Where(s => s.VariableId == variableId.Value);
        }

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
        {
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);
        }

        var results = await query.OrderBy(s => s.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return (count, results);
    }
}
