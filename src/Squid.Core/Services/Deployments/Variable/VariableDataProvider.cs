using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    Task<string> CalculateContentHashAsync(int variableSetId, CancellationToken cancellationToken);

    Task AddVariablesAsync(int variableSetId, List<Message.Domain.Deployments.Variable> variables, CancellationToken cancellationToken = default);
    Task UpdateVariablesAsync(int variableSetId, List<Message.Domain.Deployments.Variable> variables, CancellationToken cancellationToken = default);
    Task DeleteVariablesByVariableSetIdAsync(int variableSetId, CancellationToken cancellationToken = default);
    Task<List<Message.Domain.Deployments.Variable>> GetVariablesByVariableSetIdAsync(int variableSetId, CancellationToken cancellationToken = default);

    Task AddVariableScopesAsync(List<VariableScope> scopes, CancellationToken cancellationToken = default);
    Task UpdateVariableScopesAsync(int variableId, List<VariableScope> scopes, CancellationToken cancellationToken = default);
    Task DeleteVariableScopesByVariableIdAsync(int variableId, CancellationToken cancellationToken = default);
    Task<List<VariableScope>> GetVariableScopesByVariableIdAsync(int variableId, CancellationToken cancellationToken = default);
}

public class VariableDataProvider : IVariableDataProvider
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRepository _repository;
    private readonly IMapper _mapper;
    private readonly IVariableEncryptionService _encryptionService;

    public VariableDataProvider(IUnitOfWork unitOfWork, IRepository repository, IMapper mapper, IVariableEncryptionService encryptionService)
    {
        _unitOfWork = unitOfWork;
        _repository = repository;
        _mapper = mapper;
        _encryptionService = encryptionService;
    }

    public async Task AddVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(variableSet, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(variableSet, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAsync(variableSet, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<(int count, List<VariableSet>)> GetVariableSetPagingAsync(VariableSetOwnerType? ownerType = null, int? ownerId = null, int? spaceId = null, string keyword = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<VariableSet>();

        if (ownerType.HasValue)
        {
            query = query.Where(vs => vs.OwnerType == ownerType.Value);
        }

        if (ownerId.HasValue)
        {
            query = query.Where(vs => vs.OwnerId == ownerId.Value);
        }

        if (spaceId.HasValue)
        {
            query = query.Where(vs => vs.SpaceId == spaceId.Value);
        }

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
        {
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);
        }

        var results = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        return (count, results);
    }

    public async Task<VariableSet> GetVariableSetByIdAsync(int id, CancellationToken cancellationToken)
    {
        return await _repository.Query<VariableSet>(x => x.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> CalculateContentHashAsync(int variableSetId, CancellationToken cancellationToken)
    {
        var variables = await GetVariablesByVariableSetIdAsync(variableSetId, cancellationToken);

        var hashData = variables
            .OrderBy(v => v.Name)
            .Select(v => new { v.Name, v.Value, v.Type, v.IsSensitive, v.SortOrder })
            .ToList();

        var json = JsonSerializer.Serialize(hashData);
        return ComputeSha256Hash(json);
    }

    public async Task AddVariablesAsync(int variableSetId, List<Message.Domain.Deployments.Variable> variables, CancellationToken cancellationToken = default)
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

    public async Task UpdateVariablesAsync(int variableSetId, List<Message.Domain.Deployments.Variable> variables, CancellationToken cancellationToken = default)
    {
        await DeleteVariablesByVariableSetIdAsync(variableSetId, cancellationToken).ConfigureAwait(false);

        await AddVariablesAsync(variableSetId, variables, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteVariablesByVariableSetIdAsync(int variableSetId, CancellationToken cancellationToken = default)
    {
        var variables = await _repository.Query<Message.Domain.Deployments.Variable>()
            .Where(v => v.VariableSetId == variableSetId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var variable in variables)
        {
            await DeleteVariableScopesByVariableIdAsync(variable.Id, cancellationToken).ConfigureAwait(false);
            await _repository.DeleteAsync(variable, cancellationToken).ConfigureAwait(false);
        }
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Message.Domain.Deployments.Variable>> GetVariablesByVariableSetIdAsync(int variableSetId, CancellationToken cancellationToken = default)
    {
        var variables = await _repository.Query<Message.Domain.Deployments.Variable>()
            .Where(v => v.VariableSetId == variableSetId)
            .OrderBy(v => v.SortOrder)
            .ThenBy(v => v.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return await _encryptionService.DecryptSensitiveVariablesAsync(variables, variableSetId).ConfigureAwait(false);
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

    private static string ComputeSha256Hash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
