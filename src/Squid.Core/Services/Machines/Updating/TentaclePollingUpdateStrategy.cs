using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Json;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.Updating;

/// <summary>
/// TentaclePolling endpoint fields: SubscriptionId + Thumbprint (certificate
/// rotation / re-bind scenarios). Changing SubscriptionId effectively
/// re-binds the polling agent identity — operators should know what they're
/// doing; we don't block but do require the operator to pass the field
/// explicitly (no partial "reset via other-style field" bug).
///
/// <para>AgentVersion is not editable here — it's written by the
/// capabilities probe on each health check, not a user-supplied value.</para>
/// </summary>
public sealed class TentaclePollingUpdateStrategy : IMachineUpdateStrategy
{
    private static readonly IReadOnlySet<string> OwnedFields = new HashSet<string>
    {
        nameof(UpdateMachineCommand.SubscriptionId),
        nameof(UpdateMachineCommand.Thumbprint),
    };

    public bool CanHandle(string communicationStyle)
        => communicationStyle == nameof(CommunicationStyle.TentaclePolling);

    public void ValidateForStyle(int machineId, UpdateMachineCommand command)
        => CrossStyleContaminationGuard.ThrowIfCommandTouchesNonOwnedFields(
            machineId, nameof(CommunicationStyle.TentaclePolling), OwnedFields, command);

    public bool ApplyEndpointUpdate(Machine machine, UpdateMachineCommand c)
    {
        if (c.SubscriptionId == null && c.Thumbprint == null) return false;

        var endpoint = !string.IsNullOrEmpty(machine.Endpoint)
            ? JsonSerializer.Deserialize<TentaclePollingEndpointDto>(machine.Endpoint, SquidJsonDefaults.CaseInsensitive)
            : new TentaclePollingEndpointDto();

        if (c.SubscriptionId != null) endpoint.SubscriptionId = c.SubscriptionId;
        if (c.Thumbprint != null) endpoint.Thumbprint = c.Thumbprint;

        machine.Endpoint = JsonSerializer.Serialize(endpoint);
        return true;
    }
}
