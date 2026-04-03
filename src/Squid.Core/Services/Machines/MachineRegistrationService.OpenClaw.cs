using System.Text.Json;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Deployments.Machine;
using Squid.Core.Persistence.Entities.Deployments;
using CommunicationStyleEnum = Squid.Message.Enums.CommunicationStyle;

namespace Squid.Core.Services.Machines;

public partial class MachineRegistrationService
{
    public async Task<RegisterMachineResponseData> RegisterOpenClawAsync(RegisterOpenClawCommand command, CancellationToken cancellationToken = default)
    {
        var endpointJson = BuildOpenClawEndpointJson(command);
        var machine = BuildOpenClawMachine(command, endpointJson);

        await AssignDefaultPolicyAsync(machine, cancellationToken).ConfigureAwait(false);
        await _dataProvider.AddMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new RegisterMachineResponseData
        {
            MachineId = machine.Id,
        };
    }

    private static string BuildOpenClawEndpointJson(RegisterOpenClawCommand command)
    {
        return JsonSerializer.Serialize(new OpenClawEndpointDto
        {
            CommunicationStyle = nameof(CommunicationStyleEnum.OpenClaw),
            BaseUrl = command.BaseUrl,
            InlineGatewayToken = command.InlineGatewayToken,
            InlineHooksToken = command.InlineHooksToken,
            WebSocketUrl = command.WebSocketUrl,
            ResourceReferences = command.ResourceReferences
        });
    }

    private static Machine BuildOpenClawMachine(RegisterOpenClawCommand command, string endpointJson)
    {
        var serializedRoles = command.Roles != null ? JsonSerializer.Serialize(command.Roles) : null;
        var serializedEnvIds = command.EnvironmentIds != null ? JsonSerializer.Serialize(command.EnvironmentIds) : null;

        var machine = BuildMachineDefaults(
            command.MachineName ?? $"openclaw-{Guid.NewGuid():N}"[..20],
            serializedRoles, serializedEnvIds, command.SpaceId, endpointJson);

        return machine;
    }
}
