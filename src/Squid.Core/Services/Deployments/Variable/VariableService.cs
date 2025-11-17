using Squid.Core.Services.Security;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Requests.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Variable;

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
    private readonly IHybridVariableSnapshotService _hybridVariableSnapshotService;

    public VariableService(IMapper mapper, IVariableDataProvider variableDataProvider, IVariableScopeDataProvider variableScopeDataProvider, SensitiveVariableHandler sensitiveVariableHandler, IHybridVariableSnapshotService hybridVariableSnapshotService)
    {
        _mapper = mapper;
        _variableDataProvider = variableDataProvider;
        _sensitiveVariableHandler = sensitiveVariableHandler;
        _variableScopeDataProvider = variableScopeDataProvider;
        _hybridVariableSnapshotService = hybridVariableSnapshotService;
    }

    public async Task<VariableSetDto> CreateVariableSetAsync(CreateVariableSetCommand command, CancellationToken cancellationToken)
    {
        var variableSet = CreateBaseVariableSet(command);

        await _variableDataProvider.AddVariableSetAsync(variableSet, cancellationToken: cancellationToken).ConfigureAwait(false);

        await AddVariablesToSet(variableSet, command.Variables, cancellationToken).ConfigureAwait(false);

        await UpdateContentHashAsync(variableSet, cancellationToken).ConfigureAwait(false);
        await _variableDataProvider.UpdateVariableSetAsync(variableSet, cancellationToken: cancellationToken).ConfigureAwait(false);

        return _mapper.Map<VariableSetDto>(variableSet);
    }

    public async Task<VariableSetDto> UpdateVariableSetAsync(UpdateVariableSetCommand command, CancellationToken cancellationToken)
    {
        var variableSet = await GetAndValidateVariableSet(command.Id, cancellationToken).ConfigureAwait(false);

        UpdateVariableSetProperties(variableSet, command);

        await ReplaceVariablesInSet(variableSet, command.Variables, cancellationToken).ConfigureAwait(false);

        await UpdateContentHashAsync(variableSet, cancellationToken).ConfigureAwait(false);

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

        if (variableSet == null)
        {
            return null;
        }

        var variableSetDto = _mapper.Map<VariableSetDto>(variableSet);

        var variables = await _variableDataProvider.GetVariablesByVariableSetIdAsync(id, cancellationToken).ConfigureAwait(false);
        variableSetDto.Variables = _mapper.Map<List<VariableDto>>(variables);

        foreach (var variableDto in variableSetDto.Variables)
        {
            var scopes = await _variableScopeDataProvider.GetVariableScopesByVariableIdAsync(variableDto.Id, cancellationToken).ConfigureAwait(false);
            variableDto.Scopes = _mapper.Map<List<VariableScopeDto>>(scopes);
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

    private VariableSet CreateBaseVariableSet(CreateVariableSetCommand command)
    {
        var variableSet = _mapper.Map<VariableSet>(command);
        variableSet.LastModified = DateTimeOffset.UtcNow;
        return variableSet;
    }

    private async Task AddVariablesToSet(VariableSet variableSet, IEnumerable<VariableDto> variableDtos, CancellationToken cancellationToken)
    {
        if (variableDtos?.Any() != true) return;

        foreach (var dto in variableDtos)
        {
            var variable = CreateVariableFromDto(dto, variableSet.Id);
            await _variableDataProvider.AddVariablesAsync(variableSet.Id, new List<Message.Domain.Deployments.Variable> { variable }, cancellationToken).ConfigureAwait(false);

            if (dto.Scopes?.Any() == true)
            {
                var scopes = dto.Scopes.Select(s => _mapper.Map<VariableScope>(s)).ToList();
                foreach (var scope in scopes)
                {
                    scope.VariableId = variable.Id;
                }
                await _variableScopeDataProvider.AddVariableScopesAsync(scopes, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Message.Domain.Deployments.Variable CreateVariableFromDto(VariableDto dto, int variableSetId)
    {
        var variable = _mapper.Map<Message.Domain.Deployments.Variable>(dto);
        variable.VariableSetId = variableSetId;
        return variable;
    }
    
    private async Task<VariableSet> GetAndValidateVariableSet(int id, CancellationToken cancellationToken)
    {
        var variableSet = await _variableDataProvider.GetVariableSetByIdAsync(id, cancellationToken);
        if (variableSet == null)
        {
            throw new Exception($"VariableSet {id} not found");
        }
        return variableSet;
    }

    private void UpdateVariableSetProperties(VariableSet variableSet, UpdateVariableSetCommand command)
    {
        variableSet.OwnerId = command.OwnerId;
        variableSet.OwnerType = command.OwnerType;
        variableSet.SpaceId = command.SpaceId;
        variableSet.LastModified = DateTimeOffset.UtcNow;
        variableSet.Version++;
    }

    private async Task ReplaceVariablesInSet(VariableSet variableSet, IEnumerable<VariableDto> variableDtos, CancellationToken cancellationToken)
    {
        await _variableDataProvider.DeleteVariablesByVariableSetIdAsync(variableSet.Id, cancellationToken).ConfigureAwait(false);

        await AddVariablesToSet(variableSet, variableDtos, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateContentHashAsync(VariableSet variableSet, CancellationToken cancellationToken)
    {
        var (_, contentHash) = await _hybridVariableSnapshotService.CalculateVariableLatestSnapshotAsync(variableSet.Id, cancellationToken).ConfigureAwait(false);
        variableSet.ContentHash = contentHash;
    }
}
