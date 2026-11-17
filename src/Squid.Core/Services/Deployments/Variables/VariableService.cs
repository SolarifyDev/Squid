using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Variables.Exceptions;
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

    /// <summary>The sensitive variables of the sets by identity and whether each holds a value; values are never read.</summary>
    Task<IReadOnlyList<SensitiveVariableSlot>> GetSensitiveVariableSlotsAsync(IReadOnlyCollection<int> variableSetIds, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="plainValue"/> (encrypted at rest) into the ONE sensitive variable of the set with <paramref name="identity"/>,
    /// and only while it is still empty (compare-and-set): a value set by anyone in the meantime is never overwritten. Returns
    /// <see cref="SecretSlotState.Filled"/>, or why nothing was written (<see cref="SecretSlotState.Empty"/>: the set was saved concurrently
    /// twice in a row; still empty, nothing written).
    /// </summary>
    Task<SecretSlotState> FillEmptySensitiveValueAsync(int variableSetId, VariableIdentity identity, string plainValue, CancellationToken cancellationToken);

    /// <summary>
    /// Appends variables to an existing set (encrypted at rest when sensitive, after the set's last sort order) without re-saving it: the
    /// set's other variables are never read back, deleted or re-inserted. Only the set's version moves, as on any change. Returns the
    /// new variables' ids, in order.
    /// </summary>
    Task<IReadOnlyList<int>> AddVariablesAsync(int variableSetId, IReadOnlyList<VariableModel> variables, CancellationToken cancellationToken);
}

public class VariableService : IVariableService
{
    private readonly IMapper _mapper;
    private readonly IVariableDataProvider _variableDataProvider;
    private readonly IVariableScopeDataProvider _variableScopeDataProvider;
    private readonly ILibraryVariableSetDataProvider _libraryVariableSetDataProvider;

    private readonly VariableSecretCollaborators _secrets;

    public VariableService(IMapper mapper, IVariableDataProvider variableDataProvider, IVariableScopeDataProvider variableScopeDataProvider, ILibraryVariableSetDataProvider libraryVariableSetDataProvider, VariableSecretCollaborators secrets)
    {
        _mapper = mapper;
        _variableDataProvider = variableDataProvider;
        _variableScopeDataProvider = variableScopeDataProvider;
        _libraryVariableSetDataProvider = libraryVariableSetDataProvider;
        _secrets = secrets;
    }

    public async Task<VariableSetDto> CreateVariableSetAsync(CreateVariableSetCommand command, CancellationToken cancellationToken)
    {
        var variableSet = _mapper.Map<VariableSet>(command);
        await _variableDataProvider.AddVariableSetAsync(variableSet, cancellationToken: cancellationToken).ConfigureAwait(false);

        await CreateLibraryVariableSetIfApplicableAsync(variableSet, command, cancellationToken).ConfigureAwait(false);

        await AddVariablesToSetAsync(variableSet.Id, command.Variables, cancellationToken).ConfigureAwait(false);

        return _mapper.Map<VariableSetDto>(variableSet);
    }

    // Locked: the masked secrets keep what is stored, so nothing another writer has not committed yet may be read, deleted and re-inserted.
    public Task<VariableSetDto> UpdateVariableSetAsync(UpdateVariableSetCommand command, CancellationToken cancellationToken)
        => _variableDataProvider.ExecuteWithVariableSetLockedAsync(command.Id, ct => ReplaceVariableSetAsync(command, ct), cancellationToken);

    private async Task<VariableSetDto> ReplaceVariableSetAsync(UpdateVariableSetCommand command, CancellationToken cancellationToken)
    {
        var variableSet = await GetAndValidateVariableSet(command.Id, cancellationToken).ConfigureAwait(false);

        EnsureVersionUnchanged(variableSet, command.Version);

        variableSet.Name = command.Name;
        variableSet.Description = command.Description;
        variableSet.OwnerId = command.OwnerId;
        variableSet.OwnerType = command.OwnerType;
        variableSet.SpaceId = command.SpaceId;
        variableSet.Version++;

        await KeepStoredValuesOfMaskedSecretsAsync(variableSet.Id, command.Variables, cancellationToken).ConfigureAwait(false);

        await _variableDataProvider.DeleteVariablesByVariableSetIdAsync(variableSet.Id, cancellationToken).ConfigureAwait(false);

        await AddVariablesToSetAsync(variableSet.Id, command.Variables, cancellationToken).ConfigureAwait(false);

        await _variableDataProvider.UpdateVariableSetAsync(variableSet, cancellationToken: cancellationToken).ConfigureAwait(false);

        await SyncLibraryVariableSetNameAsync(variableSet, command.Name, cancellationToken).ConfigureAwait(false);

        await SettleImportedSecretsSetInSquidAsync(variableSet, cancellationToken).ConfigureAwait(false);

        return _mapper.Map<VariableSetDto>(variableSet);
    }

    // Read under the set's lock: a save based on an older version would delete and re-insert over another writer's variables.
    private static void EnsureVersionUnchanged(VariableSet variableSet, int? expectedVersion)
    {
        if (expectedVersion is { } expected && expected != variableSet.Version)
            throw new VariableSetVersionConflictException(variableSet.Id, expected, variableSet.Version);
    }

    // A secret an Octopus import left missing that this save gives a value is the user's from now on: clearing it later is an edit too.
    private async Task SettleImportedSecretsSetInSquidAsync(VariableSet variableSet, CancellationToken cancellationToken)
    {
        foreach (var hook in _secrets.SavedHooks)
            await hook.OnVariableSetSavedAsync(variableSet.SpaceId, variableSet.Id, cancellationToken).ConfigureAwait(false);
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

        return _secrets.Handler.MaskSensitiveValues(variableSetDto);
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

    public async Task<IReadOnlyList<SensitiveVariableSlot>> GetSensitiveVariableSlotsAsync(IReadOnlyCollection<int> variableSetIds, CancellationToken cancellationToken)
        => await _variableDataProvider.GetSensitiveVariableSlotsAsync(variableSetIds, cancellationToken).ConfigureAwait(false);

    // Locked like a save (the set's variables, then the set): two writers of one set always queue in the same order, never deadlock.
    public Task<SecretSlotState> FillEmptySensitiveValueAsync(int variableSetId, VariableIdentity identity, string plainValue, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(plainValue))
            throw new ArgumentException("A fill needs a value: an empty source value is never written.", nameof(plainValue));

        return _variableDataProvider.ExecuteWithVariableSetLockedAsync(variableSetId, ct => FillOnceOrRetryAsync(variableSetId, identity, plainValue, ct), cancellationToken);
    }

    private async Task<SecretSlotState> FillOnceOrRetryAsync(int variableSetId, VariableIdentity identity, string plainValue, CancellationToken cancellationToken)
    {
        var state = await TryFillOnceAsync(variableSetId, identity, plainValue, cancellationToken).ConfigureAwait(false);

        // Still empty after a lost compare-and-set: the set was saved in between (variables re-inserted with new ids). Find it again, once.
        if (state == SecretSlotState.Empty) state = await TryFillOnceAsync(variableSetId, identity, plainValue, cancellationToken).ConfigureAwait(false);

        if (state == SecretSlotState.Filled) await BumpVersionAsync(variableSetId, cancellationToken).ConfigureAwait(false);

        return state;
    }

    // Filled, why nothing can be written (AlreadySet / NotFound / Ambiguous), or Empty when the write lost to a concurrent save.
    private async Task<SecretSlotState> TryFillOnceAsync(int variableSetId, VariableIdentity identity, string plainValue, CancellationToken cancellationToken)
    {
        var slots = await _variableDataProvider.GetSensitiveVariableSlotsAsync([variableSetId], cancellationToken).ConfigureAwait(false);
        var (state, slot) = SensitiveVariableSlots.Find(slots, variableSetId, identity);

        if (state != SecretSlotState.Empty) return state;

        var written = await _variableDataProvider.SetSensitiveValueIfEmptyAsync(slot.VariableId, Encrypt(plainValue, variableSetId), cancellationToken).ConfigureAwait(false);

        return written == 0 ? SecretSlotState.Empty : SecretSlotState.Filled;
    }

    // Locked like a save (the set's variables, then the set) and the version moved: a save based on the version before the append is
    // refused (when it says which version it read) instead of re-inserting the set over the appended variables.
    public Task<IReadOnlyList<int>> AddVariablesAsync(int variableSetId, IReadOnlyList<VariableModel> variables, CancellationToken cancellationToken)
        => _variableDataProvider.ExecuteWithVariableSetLockedAsync(variableSetId, ct => AppendVariablesAsync(variableSetId, variables, ct), cancellationToken);

    private async Task<IReadOnlyList<int>> AppendVariablesAsync(int variableSetId, IReadOnlyList<VariableModel> variables, CancellationToken cancellationToken)
    {
        var existing = await _variableDataProvider.GetVariablesByVariableSetIdAsync(variableSetId, cancellationToken).ConfigureAwait(false);
        var nextSortOrder = existing.Count == 0 ? 0 : existing.Max(v => v.SortOrder) + 1;

        for (var i = 0; i < variables.Count; i++) variables[i].SortOrder = nextSortOrder + i;

        var added = await AddVariablesToSetAsync(variableSetId, variables, cancellationToken).ConfigureAwait(false);

        await BumpVersionAsync(variableSetId, cancellationToken).ConfigureAwait(false);

        return added.Select(v => v.Id).ToList();
    }

    // The page posts each secret back as the mask it read: keep what is stored (read before the delete) instead of saving the mask.
    private async Task KeepStoredValuesOfMaskedSecretsAsync(int variableSetId, List<VariableModel> incoming, CancellationToken cancellationToken)
    {
        if (!MaskedSensitiveValues.AnyMasked(incoming)) return;

        var stored = await StoredSensitiveValuesAsync(variableSetId, cancellationToken).ConfigureAwait(false);

        MaskedSensitiveValues.Restore(incoming, stored);
    }

    private async Task<List<(VariableIdentity Identity, string Value)>> StoredSensitiveValuesAsync(int variableSetId, CancellationToken cancellationToken)
    {
        var variables = await _variableDataProvider.GetVariablesByVariableSetIdAsync(variableSetId, cancellationToken).ConfigureAwait(false);
        var secrets = variables.Where(v => v.IsSensitive).ToList();

        var scopes = await _variableScopeDataProvider.GetVariableScopesByVariableIdsAsync(secrets.Select(v => v.Id).ToList(), cancellationToken).ConfigureAwait(false);
        var scopesByVariable = scopes.ToLookup(s => s.VariableId);

        return secrets.Select(v => (VariableIdentity.Of(v.Name, v.Type, scopesByVariable[v.Id].Select(s => (s.ScopeType, s.ScopeValue))), v.Value)).ToList();
    }

    private string Encrypt(string plainValue, int variableSetId)
        => (_secrets.Encryption ?? throw new InvalidOperationException("Filling a sensitive value needs the variable encryption service.")).EncryptAsync(plainValue, variableSetId);

    // Same as a save from the UI: the set's version moves, so snapshots and caches see the change.
    private async Task BumpVersionAsync(int variableSetId, CancellationToken cancellationToken)
    {
        var variableSet = await GetAndValidateVariableSet(variableSetId, cancellationToken).ConfigureAwait(false);

        variableSet.Version++;

        await _variableDataProvider.UpdateVariableSetAsync(variableSet, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<Variable>> AddVariablesToSetAsync(int variableSetId, IEnumerable<VariableModel> variableDtos, CancellationToken cancellationToken)
    {
        if (variableDtos?.Any() != true) return [];

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

        return variables;
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
