using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Services.Machines;

public interface IMachinePolicyService : IScopedDependency
{
    Task<GetMachinePoliciesResponse> GetAllAsync(CancellationToken cancellationToken = default);

    Task<GetMachinePolicyResponse> GetByIdAsync(GetMachinePolicyRequest request, CancellationToken cancellationToken = default);

    Task<SaveMachinePolicyResponse> SaveAsync(SaveMachinePolicyCommand command, CancellationToken cancellationToken = default);

    Task DeleteAsync(DeleteMachinePolicyCommand command, CancellationToken cancellationToken = default);
}

public class MachinePolicyService(IMachinePolicyDataProvider dataProvider, IMachineDataProvider machineDataProvider) : IMachinePolicyService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<GetMachinePoliciesResponse> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var policies = await dataProvider.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return new GetMachinePoliciesResponse
        {
            Data = new GetMachinePoliciesResponseData
            {
                MachinePolicies = policies.Select(ToDto).ToList()
            }
        };
    }

    public async Task<GetMachinePolicyResponse> GetByIdAsync(GetMachinePolicyRequest request, CancellationToken cancellationToken = default)
    {
        var policy = await dataProvider.GetByIdAsync(request.Id, cancellationToken).ConfigureAwait(false);

        if (policy == null)
            throw new InvalidOperationException($"MachinePolicy {request.Id} not found");

        return new GetMachinePolicyResponse { Data = ToDto(policy) };
    }

    public async Task<SaveMachinePolicyResponse> SaveAsync(SaveMachinePolicyCommand command, CancellationToken cancellationToken = default)
    {
        var dto = command.MachinePolicy;

        if (dto.Id > 0)
        {
            var existing = await dataProvider.GetByIdAsync(dto.Id, cancellationToken).ConfigureAwait(false);

            if (existing == null)
                throw new InvalidOperationException($"MachinePolicy {dto.Id} not found");

            ApplyDto(existing, dto);
            await dataProvider.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);

            return new SaveMachinePolicyResponse { Data = ToDto(existing) };
        }

        var entity = new MachinePolicy();

        ApplyDto(entity, dto);
        await dataProvider.AddAsync(entity, cancellationToken).ConfigureAwait(false);

        return new SaveMachinePolicyResponse { Data = ToDto(entity) };
    }

    public async Task DeleteAsync(DeleteMachinePolicyCommand command, CancellationToken cancellationToken = default)
    {
        var policy = await dataProvider.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);

        if (policy == null)
            throw new InvalidOperationException($"MachinePolicy {command.Id} not found");

        if (policy.IsDefault)
            throw new InvalidOperationException("Cannot delete the default machine policy");

        await ReassignMachinesToDefaultPolicyAsync(policy.Id, cancellationToken).ConfigureAwait(false);

        await dataProvider.DeleteAsync(policy, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReassignMachinesToDefaultPolicyAsync(int deletedPolicyId, CancellationToken cancellationToken)
    {
        var affectedMachines = await machineDataProvider.GetMachinesByPolicyIdAsync(deletedPolicyId, cancellationToken).ConfigureAwait(false);

        if (affectedMachines.Count == 0) return;

        var defaultPolicy = await dataProvider.GetDefaultAsync(cancellationToken).ConfigureAwait(false);

        var defaultPolicyId = defaultPolicy?.Id;

        foreach (var machine in affectedMachines)
        {
            machine.MachinePolicyId = defaultPolicyId;
            await machineDataProvider.UpdateMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        Log.Information("Reassigned {Count} machines from policy {DeletedPolicyId} to default policy {DefaultPolicyId}", affectedMachines.Count, deletedPolicyId, defaultPolicyId);
    }

    internal static MachinePolicyDto ToDto(MachinePolicy entity)
    {
        return new MachinePolicyDto
        {
            Id = entity.Id,
            SpaceId = entity.SpaceId,
            Name = entity.Name,
            Description = entity.Description,
            IsDefault = entity.IsDefault,
            MachineHealthCheckPolicy = MigrateScriptPolicyKeys(Deserialize<MachineHealthCheckPolicyDto>(entity.MachineHealthCheckPolicy) ?? new()),
            MachineConnectivityPolicy = Deserialize<MachineConnectivityPolicyDto>(entity.MachineConnectivityPolicy) ?? new(),
            MachineCleanupPolicy = Deserialize<MachineCleanupPolicyDto>(entity.MachineCleanupPolicy) ?? new(),
            MachineUpdatePolicy = Deserialize<MachineUpdatePolicyDto>(entity.MachineUpdatePolicy) ?? new(),
            MachineRpcCallRetryPolicy = Deserialize<MachineRpcCallRetryPolicyDto>(entity.MachineRpcCallRetryPolicy) ?? new()
        };
    }

    internal static void ApplyDto(MachinePolicy entity, MachinePolicyDto dto)
    {
        entity.SpaceId = dto.SpaceId;
        entity.Name = dto.Name;
        entity.Description = dto.Description;
        entity.IsDefault = dto.IsDefault;
        entity.MachineHealthCheckPolicy = Serialize(dto.MachineHealthCheckPolicy);
        entity.MachineConnectivityPolicy = Serialize(dto.MachineConnectivityPolicy);
        entity.MachineCleanupPolicy = Serialize(dto.MachineCleanupPolicy);
        entity.MachineUpdatePolicy = Serialize(dto.MachineUpdatePolicy);
        entity.MachineRpcCallRetryPolicy = Serialize(dto.MachineRpcCallRetryPolicy);
    }

    internal static MachineHealthCheckPolicyDto MigrateScriptPolicyKeys(MachineHealthCheckPolicyDto dto)
    {
        if (dto?.ScriptPolicies == null || dto.ScriptPolicies.Count == 0) return dto;

        var needsMigration = dto.ScriptPolicies.Keys.Any(IsOldCommunicationStyleKey);

        if (!needsMigration) return dto;

        var migrated = new Dictionary<string, MachineScriptPolicyDto>();

        // New-format keys first — they take priority over migrated old keys
        foreach (var (key, value) in dto.ScriptPolicies)
        {
            if (!IsOldCommunicationStyleKey(key))
                migrated.TryAdd(key, value);
        }

        // Old keys — only added if no new-format key already claimed the slot
        foreach (var (key, value) in dto.ScriptPolicies)
        {
            if (!IsOldCommunicationStyleKey(key)) continue;

            var newKey = key switch
            {
                "KubernetesApi" or "KubernetesAgent" or "Ssh" => ScriptSyntax.Bash.ToString(),
                "WindowsTentacle" => ScriptSyntax.PowerShell.ToString(),
                _ => key
            };

            migrated.TryAdd(newKey, value);
        }

        dto.ScriptPolicies = migrated;
        return dto;
    }

    private static bool IsOldCommunicationStyleKey(string key)
        => key is "KubernetesApi" or "KubernetesAgent" or "Ssh" or "WindowsTentacle";

    internal static T Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Serialize<T>(T obj) where T : class
    {
        if (obj == null) return null;

        return JsonSerializer.Serialize(obj, JsonOptions);
    }
}
