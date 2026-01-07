using System.Security.Cryptography;
using System.Text;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Security;
using Squid.Message.Enums;

namespace Squid.Core.Services.Deployments.Variable;

public interface IVariableDataProvider : IScopedDependency
{
    Task AddVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<VariableSet>)> GetVariableSetPagingAsync(VariableSetOwnerType? ownerType = null, int? ownerId = null, int? spaceId = null, string keyword = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<VariableSet> GetVariableSetByIdAsync(int id, CancellationToken cancellationToken);
    
    Task<List<VariableSet>> GetVariableSetsByIdsAsync(List<int> ids, CancellationToken cancellationToken);

    Task AddVariablesAsync(int variableSetId, List<Persistence.Entities.Deployments.Variable> variables, CancellationToken cancellationToken = default);
    
    Task UpdateVariablesAsync(int variableSetId, List<Persistence.Entities.Deployments.Variable> variables, CancellationToken cancellationToken = default);
    
    Task DeleteVariablesByVariableSetIdAsync(int variableSetId, CancellationToken cancellationToken = default);
    
    Task<List<Persistence.Entities.Deployments.Variable>> GetVariablesByVariableSetIdAsync(int variableSetId, CancellationToken cancellationToken = default);
    
    Task<List<Persistence.Entities.Deployments.Variable>> GetVariablesByVariableSetIdsAsync(List<int> variableSetIds, CancellationToken cancellationToken = default);
}

public partial class VariableDataProvider : IVariableDataProvider
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

    public async Task<List<VariableSet>> GetVariableSetsByIdsAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await _repository.Query<VariableSet>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddVariablesAsync(int variableSetId, List<Persistence.Entities.Deployments.Variable> variables, CancellationToken cancellationToken = default)
    {
        var encryptedVariables = await _encryptionService.EncryptSensitiveVariablesAsync(variables, variableSetId).ConfigureAwait(false);

        foreach (var variable in encryptedVariables)
        {
            variable.VariableSetId = variableSetId;
            variable.LastModifiedOn = DateTimeOffset.UtcNow;
            await _repository.InsertAsync(variable, cancellationToken).ConfigureAwait(false);
        }
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateVariablesAsync(int variableSetId, List<Persistence.Entities.Deployments.Variable> variables, CancellationToken cancellationToken = default)
    {
        await DeleteVariablesByVariableSetIdAsync(variableSetId, cancellationToken).ConfigureAwait(false);

        await AddVariablesAsync(variableSetId, variables, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteVariablesByVariableSetIdAsync(int variableSetId, CancellationToken cancellationToken = default)
    {
        var variables = await _repository.Query<Persistence.Entities.Deployments.Variable>()
            .Where(v => v.VariableSetId == variableSetId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var variable in variables)
        {
            await _variableScopeDataProvider.DeleteVariableScopesByVariableIdAsync(variable.Id, cancellationToken).ConfigureAwait(false);
            await _repository.DeleteAsync(variable, cancellationToken).ConfigureAwait(false);
        }
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Persistence.Entities.Deployments.Variable>> GetVariablesByVariableSetIdAsync(int variableSetId, CancellationToken cancellationToken = default)
    {
        var variables = await _repository.Query<Persistence.Entities.Deployments.Variable>()
            .Where(v => v.VariableSetId == variableSetId)
            .OrderBy(v => v.SortOrder)
            .ThenBy(v => v.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return await _encryptionService.DecryptSensitiveVariablesAsync(variables, variableSetId).ConfigureAwait(false);
    }

    public async Task<List<Persistence.Entities.Deployments.Variable>> GetVariablesByVariableSetIdsAsync(List<int> variableSetIds, CancellationToken cancellationToken = default)
    {
        var variables = await _repository.Query<Persistence.Entities.Deployments.Variable>()
            .Where(v => variableSetIds.Contains(v.VariableSetId))
            .OrderBy(v => v.SortOrder)
            .ThenBy(v => v.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<Persistence.Entities.Deployments.Variable>();

        foreach (var variableSetId in variableSetIds)
        {
            result.AddRange(await _encryptionService.DecryptSensitiveVariablesAsync(
                variables.Where(x => x.VariableSetId == variableSetId).ToList(), variableSetId).ConfigureAwait(false));
        }
        
        return result;
    }
}
