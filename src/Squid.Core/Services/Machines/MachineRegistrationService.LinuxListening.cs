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
        var resolvedEnvironmentIds = await ResolveEnvironmentIdsAsync(command.Environments, cancellationToken).ConfigureAwait(false);

        var existing = await _dataProvider.GetMachineByEndpointUriAsync(command.Uri, cancellationToken).ConfigureAwait(false);

        Machine machine;

        var serializedRoles = SerializeRolesFromCsv(command.Roles);

        if (existing != null)
        {
            existing.Endpoint = BuildLinuxListeningEndpointJson(command);

            if (serializedRoles != null) existing.Roles = serializedRoles;
            if (resolvedEnvironmentIds != null) existing.EnvironmentIds = resolvedEnvironmentIds;

            await _dataProvider.UpdateMachineAsync(existing, cancellationToken: cancellationToken).ConfigureAwait(false);

            machine = existing;

            Log.Information("Updated existing LinuxListening machine {MachineName} (Uri={Uri})", machine.Name, command.Uri);
        }
        else
        {
            machine = BuildLinuxListeningMachine(command, BuildLinuxListeningEndpointJson(command), serializedRoles, resolvedEnvironmentIds);

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

    private static Machine BuildLinuxListeningMachine(RegisterLinuxListeningCommand command, string endpointJson, string serializedRoles, string resolvedEnvironmentIds)
    {
        return BuildMachineDefaults(
            command.MachineName ?? $"linux-{Guid.NewGuid():N}"[..20],
            serializedRoles, resolvedEnvironmentIds, command.SpaceId, endpointJson, command.MachinePolicyId);
    }
}
