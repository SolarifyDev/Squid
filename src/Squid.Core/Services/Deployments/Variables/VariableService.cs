using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Security;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Requests.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Variables;

public interface IVariableService : IScopedDependency
{
    Task<VariableSetDto> CreateVariableSetAsync(CreateVariableSetCommand command, CancellationToken cancellationToken);

    Task<VariableSetDto> UpdateVariableSetAsync(UpdateVariableSetCommand command, CancellationToken cancellationToken);

    Task DeleteVariableSetAsync(int id, CancellationToken cancellationToken);

    Task<VariableSetDto> GetVariableSetByIdAsync(int id, CancellationToken cancellationToken);

    Task<GetVariableSetsResponse> GetVariableSetsAsync(GetVariableSetsRequest request, CancellationToken cancellationToken);
}

public class VariableService : IVariableService
{
    private readonly IMapper _mapper;
    private readonly IVariableDataProvider _variableDataProvider;
    private readonly SensitiveVariableHandler _sensitiveVariableHandler;
    private readonly IVariableScopeDataProvider _variableScopeDataProvider;

    public VariableService(IMapper mapper, IVariableDataProvider variableDataProvider, IVariableScopeDataProvider variableScopeDataProvider, SensitiveVariableHandler sensitiveVariableHandler)
    {
        _mapper = mapper;
        _variableDataProvider = variableDataProvider;
        _sensitiveVariableHandler = sensitiveVariableHandler;
        _variableScopeDataProvider = variableScopeDataProvider;
    }

    public async Task<VariableSetDto> CreateVariableSetAsync(CreateVariableSetCommand command, CancellationToken cancellationToken)
    {
        var variableSet = _mapper.Map<VariableSet>(command);
        variableSet.LastModified = DateTimeOffset.UtcNow;

        await _variableDataProvider.AddVariableSetAsync(variableSet, cancellationToken: cancellationToken).ConfigureAwait(false);

        await AddVariablesToSetAsync(variableSet.Id, command.Variables, cancellationToken).ConfigureAwait(false);

        return _mapper.Map<VariableSetDto>(variableSet);
    }

    public async Task<VariableSetDto> UpdateVariableSetAsync(UpdateVariableSetCommand command, CancellationToken cancellationToken)
    {
        var variableSet = await GetAndValidateVariableSet(command.Id, cancellationToken).ConfigureAwait(false);

        variableSet.Name = command.Name;
        variableSet.Description = command.Description;
        variableSet.OwnerId = command.OwnerId;
        variableSet.OwnerType = command.OwnerType;
        variableSet.SpaceId = command.SpaceId;
        variableSet.LastModified = DateTimeOffset.UtcNow;
        variableSet.Version++;

        await _variableDataProvider.DeleteVariablesByVariableSetIdAsync(variableSet.Id, cancellationToken).ConfigureAwait(false);

        await AddVariablesToSetAsync(variableSet.Id, command.Variables, cancellationToken).ConfigureAwait(false);

        await _variableDataProvider.UpdateVariableSetAsync(variableSet, cancellationToken: cancellationToken).ConfigureAwait(false);

        return _mapper.Map<VariableSetDto>(variableSet);
    }

    public async Task DeleteVariableSetAsync(int id, CancellationToken cancellationToken)
    {
        var variableSet = await GetAndValidateVariableSet(id, cancellationToken).ConfigureAwait(false);

        await _variableDataProvider.DeleteVariableSetAsync(variableSet, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<VariableSetDto> GetVariableSetByIdAsync(int id, CancellationToken cancellationToken)
    {
        var variableSet = await _variableDataProvider.GetVariableSetByIdAsync(id, cancellationToken).ConfigureAwait(false);

        if (variableSet == null) return null;

        var variableSetDto = _mapper.Map<VariableSetDto>(variableSet);
        var variables = await _variableDataProvider.GetVariablesByVariableSetIdAsync(id, cancellationToken).ConfigureAwait(false);

        variableSetDto.Variables = _mapper.Map<List<VariableDto>>(variables);

        var variableIds = variableSetDto.Variables.Select(v => v.Id).ToList();
        var allScopes = await _variableScopeDataProvider.GetVariableScopesByVariableIdsAsync(variableIds, cancellationToken).ConfigureAwait(false);
        var scopesByVariableId = allScopes.GroupBy(s => s.VariableId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var variableDto in variableSetDto.Variables)
        {
            variableDto.Scopes = scopesByVariableId.TryGetValue(variableDto.Id, out var scopes)
                ? _mapper.Map<List<VariableScopeDto>>(scopes)
                : new();
        }

        return _sensitiveVariableHandler.MaskSensitiveValues(variableSetDto);
    }

    public async Task<GetVariableSetsResponse> GetVariableSetsAsync(GetVariableSetsRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _variableDataProvider.GetVariableSetPagingAsync(request.OwnerType, request.OwnerId, request.SpaceId, request.Keyword, request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetVariableSetsResponse
        {
            Data = new GetVariableSetsResponseData
            {
                Count = count,
                VariableSets = _mapper.Map<List<VariableSetDto>>(data)
            }
        };
    }

    private async Task AddVariablesToSetAsync(int variableSetId, IEnumerable<VariableDto> variableDtos, CancellationToken cancellationToken)
    {
        if (variableDtos?.Any() != true) return;

        var dtoList = variableDtos.ToList();

        var variables = dtoList.Select(dto =>
        {
            var variable = _mapper.Map<Variable>(dto);
            variable.VariableSetId = variableSetId;
            return variable;
        }).ToList();

        await _variableDataProvider.AddVariablesAsync(variableSetId, variables, cancellationToken).ConfigureAwait(false);

        var allScopes = new List<VariableScope>();

        for (var i = 0; i < dtoList.Count; i++)
        {
            if (dtoList[i].Scopes?.Any() != true) continue;

            foreach (var scopeDto in dtoList[i].Scopes)
            {
                var scope = _mapper.Map<VariableScope>(scopeDto);
                scope.VariableId = variables[i].Id;
                allScopes.Add(scope);
            }
        }

        if (allScopes.Count > 0)
            await _variableScopeDataProvider.AddVariableScopesAsync(allScopes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VariableSet> GetAndValidateVariableSet(int id, CancellationToken cancellationToken)
    {
        var variableSet = await _variableDataProvider.GetVariableSetByIdAsync(id, cancellationToken).ConfigureAwait(false);

        if (variableSet == null)
            throw new Exception($"VariableSet {id} not found");

        return variableSet;
    }
}
