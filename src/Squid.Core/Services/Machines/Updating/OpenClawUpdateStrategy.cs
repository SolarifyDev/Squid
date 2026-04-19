using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Json;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.Updating;

public sealed class OpenClawUpdateStrategy : IMachineUpdateStrategy
{
    private static readonly IReadOnlySet<string> OwnedFields = new HashSet<string>
    {
        nameof(UpdateMachineCommand.BaseUrl),
        nameof(UpdateMachineCommand.InlineGatewayToken),
        nameof(UpdateMachineCommand.InlineHooksToken),
        nameof(UpdateMachineCommand.ResourceReferences),
    };

    public bool CanHandle(string communicationStyle)
        => communicationStyle == nameof(CommunicationStyle.OpenClaw);

    public void ValidateForStyle(int machineId, UpdateMachineCommand command)
        => CrossStyleContaminationGuard.ThrowIfCommandTouchesNonOwnedFields(
            machineId, nameof(CommunicationStyle.OpenClaw), OwnedFields, command);

    public bool ApplyEndpointUpdate(Machine machine, UpdateMachineCommand c)
    {
        if (c.BaseUrl == null && c.InlineGatewayToken == null && c.InlineHooksToken == null && c.ResourceReferences == null)
            return false;

        var endpoint = !string.IsNullOrEmpty(machine.Endpoint)
            ? JsonSerializer.Deserialize<OpenClawEndpointDto>(machine.Endpoint, SquidJsonDefaults.CaseInsensitive)
            : new OpenClawEndpointDto();

        if (c.BaseUrl != null) endpoint.BaseUrl = c.BaseUrl;
        if (c.InlineGatewayToken != null) endpoint.InlineGatewayToken = c.InlineGatewayToken;
        if (c.InlineHooksToken != null) endpoint.InlineHooksToken = c.InlineHooksToken;
        if (c.ResourceReferences != null) endpoint.ResourceReferences = c.ResourceReferences;

        machine.Endpoint = JsonSerializer.Serialize(endpoint);
        return true;
    }
}
