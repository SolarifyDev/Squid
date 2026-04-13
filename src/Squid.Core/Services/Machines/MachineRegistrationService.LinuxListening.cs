using System.Text.Json;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Deployments.Machine;
using Squid.Core.Persistence.Entities.Deployments;
using CommunicationStyleEnum = Squid.Message.Enums.CommunicationStyle;

namespace Squid.Core.Services.Machines;

public partial class MachineRegistrationService
{
    public async Task<RegisterMachineResponseData> RegisterLinuxListeningAsync(RegisterLinuxListeningCommand command, CancellationToken cancellationToken = default)
    {
        var endpointJson = BuildLinuxListeningEndpointJson(command);
        var machine = BuildLinuxListeningMachine(command, endpointJson);

        await EnsureUniqueNameAsync(machine.Name, command.SpaceId, cancellationToken).ConfigureAwait(false);
        await AssignDefaultPolicyAsync(machine, cancellationToken).ConfigureAwait(false);
        await _dataProvider.AddMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

        Log.Information("Registered LinuxListening machine {MachineName} (Uri={Uri})", machine.Name, command.Uri);

        return new RegisterMachineResponseData
        {
            MachineId = machine.Id
        };
    }

    private static string BuildLinuxListeningEndpointJson(RegisterLinuxListeningCommand command)
    {
        return JsonSerializer.Serialize(new TentacleListeningEndpointDto
        {
            CommunicationStyle = nameof(CommunicationStyleEnum.LinuxListening),
            Uri = command.Uri,
            Thumbprint = command.Thumbprint,
            AgentVersion = command.AgentVersion
        });
    }

    private static Machine BuildLinuxListeningMachine(RegisterLinuxListeningCommand command, string endpointJson)
    {
        var serializedRoles = command.Roles != null ? JsonSerializer.Serialize(command.Roles) : null;
        var serializedEnvIds = command.EnvironmentIds != null ? JsonSerializer.Serialize(command.EnvironmentIds) : null;

        return BuildMachineDefaults(
            command.MachineName ?? $"linux-{Guid.NewGuid():N}"[..20],
            serializedRoles, serializedEnvIds, command.SpaceId, endpointJson);
    }
}
