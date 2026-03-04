using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Security;
using Squid.Message.Enums;

namespace Squid.Core.Services.Deployments.Variables;

public interface IVariableDataProvider : IScopedDependency
{
    Task AddVariablesAsync(int variableSetId, List<Variable> variables, CancellationToken cancellationToken = default);

    Task DeleteVariablesByVariableSetIdAsync(int variableSetId, CancellationToken cancellationToken = default);

    Task<List<Variable>> GetVariablesByVariableSetIdAsync(int variableSetId, CancellationToken cancellationToken = default);

    Task<List<Variable>> GetVariablesByVariableSetIdsAsync(List<int> variableSetIds, CancellationToken cancellationToken = default);

    Task AddVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<VariableSet>)> GetVariableSetPagingAsync(VariableSetOwnerType? ownerType = null, int? ownerId = null, int? spaceId = null, string keyword = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<VariableSet> GetVariableSetByIdAsync(int id, CancellationToken cancellationToken);

    Task<VariableSet> GetVariableSetByOwnerAsync(int ownerId, VariableSetOwnerType ownerType, CancellationToken cancellationToken = default);

    Task<List<VariableSet>> GetVariableSetsByOwnerIdsAsync(List<int> ownerIds, VariableSetOwnerType ownerType, CancellationToken cancellationToken = default);
}

public class VariableDataProvider : IVariableDataProvider
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRepository _repository;
    private readonly IMapper _mapper;
    private readonly IVariableEncryptionService _encryptionService;
    private readonly IVariableScopeDataProvider _variableScopeDataProvider;

    public VariableDataProvider(IUnitOfWork unitOfWork, IRepository repository, IMapper mapper, IVariableEncryptionService encryptionService, IVariableScopeDataProvider variableScopeDataProvider)
    {
        _unitOfWork = unitOfWork;
        _repository = repository;
        _mapper = mapper;
        _encryptionService = encryptionService;
        _variableScopeDataProvider = variableScopeDataProvider;
    }

    public async Task AddVariablesAsync(int variableSetId, List<Variable> variables, CancellationToken cancellationToken = default)
    {
        if (variables.Count == 0) return;

        var encryptedVariables = await _encryptionService.EncryptSensitiveVariablesAsync(variables, variableSetId).ConfigureAwait(false);

        foreach (var variable in encryptedVariables)
        {
            variable.VariableSetId = variableSetId;
            variable.LastModifiedOn = DateTimeOffset.UtcNow;
        }

        await _repository.InsertAllAsync(encryptedVariables, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < variables.Count; i++)
            variables[i].Id = encryptedVariables[i].Id;
    }

    public async Task DeleteVariablesByVariableSetIdAsync(int variableSetId, CancellationToken cancellationToken = default)
    {
        var variables = await _repository.Query<Variable>()
            .Where(v => v.VariableSetId == variableSetId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (variables.Count == 0) return;

        var variableIds = variables.Select(v => v.Id).ToList();
        var scopes = await _variableScopeDataProvider.GetVariableScopesByVariableIdsAsync(variableIds, cancellationToken).ConfigureAwait(false);

        if (scopes.Count > 0)
            await _repository.DeleteAllAsync(scopes, cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(variables, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Variable>> GetVariablesByVariableSetIdAsync(int variableSetId, CancellationToken cancellationToken = default)
    {
        var variables = await _repository.Query<Variable>()
            .Where(v => v.VariableSetId == variableSetId)
            .OrderBy(v => v.SortOrder)
            .ThenBy(v => v.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return await _encryptionService.DecryptSensitiveVariablesAsync(variables, variableSetId).ConfigureAwait(false);
    }

    public async Task<List<Variable>> GetVariablesByVariableSetIdsAsync(List<int> variableSetIds, CancellationToken cancellationToken = default)
    {
        var variables = await _repository.Query<Variable>()
            .Where(v => variableSetIds.Contains(v.VariableSetId))
            .OrderBy(v => v.SortOrder)
            .ThenBy(v => v.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<Variable>();

        foreach (var variableSetId in variableSetIds)
        {
            result.AddRange(await _encryptionService.DecryptSensitiveVariablesAsync(
                variables.Where(x => x.VariableSetId == variableSetId).ToList(), variableSetId).ConfigureAwait(false));
        }

        return result;
    }

    public async Task AddVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(variableSet, cancellationToken).ConfigureAwait(false);

        if (forceSave)
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(variableSet, cancellationToken).ConfigureAwait(false);

        if (forceSave)
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAsync(variableSet, cancellationToken).ConfigureAwait(false);

        if (forceSave)
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int count, List<VariableSet>)> GetVariableSetPagingAsync(VariableSetOwnerType? ownerType = null, int? ownerId = null, int? spaceId = null, string keyword = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<VariableSet>();

        if (ownerType.HasValue)
            query = query.Where(vs => vs.OwnerType == ownerType.Value);

        if (ownerId.HasValue)
            query = query.Where(vs => vs.OwnerId == ownerId.Value);

        if (spaceId.HasValue)
            query = query.Where(vs => vs.SpaceId == spaceId.Value);

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);

        var results = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        return (count, results);
    }

    public async Task<VariableSet> GetVariableSetByIdAsync(int id, CancellationToken cancellationToken)
    {
        return await _repository.Query<VariableSet>(x => x.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
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
}
