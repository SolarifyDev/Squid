using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Json;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.Updating;

/// <summary>
/// TentacleListening endpoint fields: Uri (machine IP change), Thumbprint
/// (cert rotation), ProxyId (proxy swap).
/// </summary>
public sealed class TentacleListeningUpdateStrategy : IMachineUpdateStrategy
{
    private static readonly IReadOnlySet<string> OwnedFields = new HashSet<string>
    {
        nameof(UpdateMachineCommand.Uri),
        nameof(UpdateMachineCommand.Thumbprint),
        nameof(UpdateMachineCommand.ProxyId),
    };

    public bool CanHandle(string communicationStyle)
        => communicationStyle == nameof(CommunicationStyle.TentacleListening);

    public void ValidateForStyle(int machineId, UpdateMachineCommand command)
        => CrossStyleContaminationGuard.ThrowIfCommandTouchesNonOwnedFields(
            machineId, nameof(CommunicationStyle.TentacleListening), OwnedFields, command);

    public bool ApplyEndpointUpdate(Machine machine, UpdateMachineCommand c)
    {
        if (c.Uri == null && c.Thumbprint == null && !c.ProxyId.HasValue) return false;

        var endpoint = !string.IsNullOrEmpty(machine.Endpoint)
            ? JsonSerializer.Deserialize<TentacleListeningEndpointDto>(machine.Endpoint, SquidJsonDefaults.CaseInsensitive)
            : new TentacleListeningEndpointDto();

        if (c.Uri != null) endpoint.Uri = c.Uri;
        if (c.Thumbprint != null) endpoint.Thumbprint = c.Thumbprint;
        if (c.ProxyId.HasValue) endpoint.ProxyId = c.ProxyId.Value;

        machine.Endpoint = JsonSerializer.Serialize(endpoint);
        return true;
    }
}
