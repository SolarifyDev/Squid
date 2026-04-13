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
        var existing = await _dataProvider.GetMachineByEndpointUriAsync(command.Uri, cancellationToken).ConfigureAwait(false);

        Machine machine;

        if (existing != null)
        {
            existing.Endpoint = BuildLinuxListeningEndpointJson(command);

            var serializedRoles = command.Roles != null ? JsonSerializer.Serialize(command.Roles) : null;
            var serializedEnvIds = command.EnvironmentIds != null ? JsonSerializer.Serialize(command.EnvironmentIds) : null;

            if (serializedRoles != null) existing.Roles = serializedRoles;
            if (serializedEnvIds != null) existing.EnvironmentIds = serializedEnvIds;

            await _dataProvider.UpdateMachineAsync(existing, cancellationToken: cancellationToken).ConfigureAwait(false);

            machine = existing;

            Log.Information("Updated existing LinuxListening machine {MachineName} (Uri={Uri})", machine.Name, command.Uri);
        }
        else
        {
            machine = BuildLinuxListeningMachine(command, BuildLinuxListeningEndpointJson(command));

            await EnsureUniqueNameAsync(machine.Name, command.SpaceId, cancellationToken).ConfigureAwait(false);
            await AssignDefaultPolicyAsync(machine, cancellationToken).ConfigureAwait(false);
            await _dataProvider.AddMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

            Log.Information("Registered new LinuxListening machine {MachineName} (Uri={Uri})", machine.Name, command.Uri);
        }

        return new RegisterMachineResponseData
        {
            MachineId = machine.Id,
            ServerThumbprint = GetServerThumbprint()
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
