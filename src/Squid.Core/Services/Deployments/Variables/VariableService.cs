using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Security;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Enums;
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
    private readonly ILibraryVariableSetDataProvider _libraryVariableSetDataProvider;

    public VariableService(IMapper mapper, IVariableDataProvider variableDataProvider, IVariableScopeDataProvider variableScopeDataProvider, SensitiveVariableHandler sensitiveVariableHandler, ILibraryVariableSetDataProvider libraryVariableSetDataProvider)
    {
        _mapper = mapper;
        _variableDataProvider = variableDataProvider;
        _sensitiveVariableHandler = sensitiveVariableHandler;
        _variableScopeDataProvider = variableScopeDataProvider;
        _libraryVariableSetDataProvider = libraryVariableSetDataProvider;
    }

    public async Task<VariableSetDto> CreateVariableSetAsync(CreateVariableSetCommand command, CancellationToken cancellationToken)
    {
        var variableSet = _mapper.Map<VariableSet>(command);
        await _variableDataProvider.AddVariableSetAsync(variableSet, cancellationToken: cancellationToken).ConfigureAwait(false);

        await CreateLibraryVariableSetIfApplicableAsync(variableSet, command, cancellationToken).ConfigureAwait(false);

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
        variableSet.Version++;

        await _variableDataProvider.DeleteVariablesByVariableSetIdAsync(variableSet.Id, cancellationToken).ConfigureAwait(false);

        await AddVariablesToSetAsync(variableSet.Id, command.Variables, cancellationToken).ConfigureAwait(false);

        await _variableDataProvider.UpdateVariableSetAsync(variableSet, cancellationToken: cancellationToken).ConfigureAwait(false);

        await SyncLibraryVariableSetNameAsync(variableSet, command.Name, cancellationToken).ConfigureAwait(false);

        return _mapper.Map<VariableSetDto>(variableSet);
    }

    public async Task DeleteVariableSetAsync(int id, CancellationToken cancellationToken)
    {
        var variableSet = await GetAndValidateVariableSet(id, cancellationToken).ConfigureAwait(false);

        await DeleteLibraryVariableSetIfApplicableAsync(variableSet, cancellationToken).ConfigureAwait(false);

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

        await EnsureLibraryVariableSetsAsync(data, cancellationToken).ConfigureAwait(false);

        var dtos = _mapper.Map<List<VariableSetDto>>(data);

        return new GetVariableSetsResponse
        {
            Data = new GetVariableSetsResponseData
            {
                Count = count,
                VariableSets = dtos
            }
        };
    }

    private async Task AddVariablesToSetAsync(int variableSetId, IEnumerable<VariableModel> variableDtos, CancellationToken cancellationToken)
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

    private async Task EnsureLibraryVariableSetsAsync(List<VariableSet> variableSets, CancellationToken ct)
    {
        var libraryTypeSets = variableSets.Where(vs => vs.OwnerType == VariableSetOwnerType.LibraryVariableSet).ToList();
        if (libraryTypeSets.Count == 0) return;

        var ownerIds = libraryTypeSets.Where(vs => vs.OwnerId > 0).Select(vs => vs.OwnerId).Distinct().ToList();
        var existingRecords = ownerIds.Count > 0
            ? await _libraryVariableSetDataProvider.GetByIdsAsync(ownerIds, ct).ConfigureAwait(false)
            : new List<LibraryVariableSet>();
        var existingIds = new HashSet<int>(existingRecords.Select(r => r.Id));

        foreach (var vs in libraryTypeSets)
        {
            if (vs.OwnerId > 0 && existingIds.Contains(vs.OwnerId)) continue;

            var lvs = new LibraryVariableSet
            {
                Name = vs.Name ?? string.Empty,
                VariableSetId = vs.Id,
                ContentType = "Variables",
                Json = string.Empty,
                SpaceId = vs.SpaceId,
                Version = 1
            };

            await _libraryVariableSetDataProvider.AddAsync(lvs, ct: ct).ConfigureAwait(false);

            vs.OwnerId = lvs.Id;
            await _variableDataProvider.UpdateVariableSetAsync(vs, cancellationToken: ct).ConfigureAwait(false);
        }
    }

    private async Task<LibraryVariableSet> CreateLibraryVariableSetIfApplicableAsync(VariableSet variableSet, CreateVariableSetCommand command, CancellationToken ct)
    {
        if (variableSet.OwnerType != VariableSetOwnerType.LibraryVariableSet)
            return null;

        var libraryVariableSet = new LibraryVariableSet
        {
            Name = command.Name ?? string.Empty,
            VariableSetId = variableSet.Id,
            ContentType = "Variables",
            Json = string.Empty,
            SpaceId = variableSet.SpaceId,
            Version = 1
        };

        await _libraryVariableSetDataProvider.AddAsync(libraryVariableSet, ct: ct).ConfigureAwait(false);

        variableSet.OwnerId = libraryVariableSet.Id;
        await _variableDataProvider.UpdateVariableSetAsync(variableSet, cancellationToken: ct).ConfigureAwait(false);

        return libraryVariableSet;
    }

    private async Task SyncLibraryVariableSetNameAsync(VariableSet variableSet, string newName, CancellationToken ct)
    {
        if (variableSet.OwnerType != VariableSetOwnerType.LibraryVariableSet) return;
        if (variableSet.OwnerId <= 0) return;

        var lvs = await _libraryVariableSetDataProvider.GetByIdAsync(variableSet.OwnerId, ct).ConfigureAwait(false);

        if (lvs == null) return;

        lvs.Name = newName ?? string.Empty;

        await _libraryVariableSetDataProvider.UpdateAsync(lvs, ct: ct).ConfigureAwait(false);
    }

    private async Task DeleteLibraryVariableSetIfApplicableAsync(VariableSet variableSet, CancellationToken ct)
    {
        if (variableSet.OwnerType != VariableSetOwnerType.LibraryVariableSet)
            return;

        var libraryVariableSet = await _libraryVariableSetDataProvider.GetByIdAsync(variableSet.OwnerId, ct).ConfigureAwait(false);

        if (libraryVariableSet != null)
            await _libraryVariableSetDataProvider.DeleteAsync(libraryVariableSet, ct: ct).ConfigureAwait(false);
    }

}
