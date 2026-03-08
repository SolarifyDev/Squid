using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Machine;
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

public class MachinePolicyService(IMachinePolicyDataProvider dataProvider) : IMachinePolicyService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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

        await dataProvider.DeleteAsync(policy, cancellationToken).ConfigureAwait(false);
    }

    private static MachinePolicyDto ToDto(MachinePolicy entity)
    {
        return new MachinePolicyDto
        {
            Id = entity.Id,
            SpaceId = entity.SpaceId,
            Name = entity.Name,
            Description = entity.Description,
            IsDefault = entity.IsDefault,
            MachineHealthCheckPolicy = Deserialize<MachineHealthCheckPolicyDto>(entity.MachineHealthCheckPolicy) ?? new(),
            MachineConnectivityPolicy = Deserialize<MachineConnectivityPolicyDto>(entity.MachineConnectivityPolicy) ?? new(),
            MachineCleanupPolicy = Deserialize<MachineCleanupPolicyDto>(entity.MachineCleanupPolicy) ?? new(),
            MachineUpdatePolicy = Deserialize<MachineUpdatePolicyDto>(entity.MachineUpdatePolicy) ?? new()
        };
    }

    private static void ApplyDto(MachinePolicy entity, MachinePolicyDto dto)
    {
        entity.SpaceId = dto.SpaceId;
        entity.Name = dto.Name;
        entity.Description = dto.Description;
        entity.IsDefault = dto.IsDefault;
        entity.MachineHealthCheckPolicy = Serialize(dto.MachineHealthCheckPolicy);
        entity.MachineConnectivityPolicy = Serialize(dto.MachineConnectivityPolicy);
        entity.MachineCleanupPolicy = Serialize(dto.MachineCleanupPolicy);
        entity.MachineUpdatePolicy = Serialize(dto.MachineUpdatePolicy);
    }

    private static T Deserialize<T>(string json) where T : class
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

    private static string Serialize<T>(T obj) where T : class
    {
        if (obj == null) return null;

        return JsonSerializer.Serialize(obj, JsonOptions);
    }
}
