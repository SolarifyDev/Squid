using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Deployments.Machine;
using CommunicationStyleEnum = Squid.Message.Enums.CommunicationStyle;

namespace Squid.Core.Services.Machines;

public partial class MachineRegistrationService
{
    public async Task<RegisterMachineResponseData> RegisterLinuxPollingAsync(RegisterLinuxPollingCommand command, CancellationToken cancellationToken = default)
    {
        var resolvedEnvironmentIds = await ResolveEnvironmentIdsAsync(command.Environments, cancellationToken).ConfigureAwait(false);

        var existing = await _dataProvider.GetMachineBySubscriptionIdAsync(command.SubscriptionId, cancellationToken).ConfigureAwait(false);

        Machine machine;

        var serializedRoles = SerializeRolesFromCsv(command.Roles);

        if (existing != null)
        {
            var agentVersion = command.AgentVersion ?? EndpointJsonHelper.GetField(existing.Endpoint, "AgentVersion");

            existing.Roles = serializedRoles ?? existing.Roles;
            existing.EnvironmentIds = resolvedEnvironmentIds ?? existing.EnvironmentIds;
            existing.Endpoint = BuildTentaclePollingEndpointJson(command, agentVersion);

            await _dataProvider.UpdateMachineAsync(existing, cancellationToken: cancellationToken).ConfigureAwait(false);

            machine = existing;

            Log.Information("Updated existing Tentacle machine {MachineName} ({SubscriptionId})", machine.Name, command.SubscriptionId);
        }
        else
        {
            machine = BuildTentaclePollingMachine(command, resolvedEnvironmentIds);

            await EnsureUniqueNameAsync(machine.Name, machine.SpaceId, cancellationToken).ConfigureAwait(false);
            await AssignDefaultPolicyAsync(machine, cancellationToken).ConfigureAwait(false);
            await _dataProvider.AddMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

            Log.Information("Registered new Tentacle machine {MachineName} ({SubscriptionId})", machine.Name, command.SubscriptionId);
        }

        _trustDistributor.Reconfigure();

        return new RegisterMachineResponseData
        {
            MachineId = machine.Id,
            ServerThumbprint = GetServerThumbprint(),
            SubscriptionUri = $"poll://{command.SubscriptionId}/"
        };
    }

    private static string BuildTentaclePollingEndpointJson(RegisterLinuxPollingCommand command, string agentVersion)
    {
        return JsonSerializer.Serialize(new TentaclePollingEndpointDto
        {
            Thumbprint = command.Thumbprint,
            SubscriptionId = command.SubscriptionId,
            CommunicationStyle = nameof(CommunicationStyleEnum.LinuxPolling),
            AgentVersion = agentVersion
        });
    }

    private Machine BuildTentaclePollingMachine(RegisterLinuxPollingCommand command, string resolvedEnvironmentIds)
    {
        var endpointJson = BuildTentaclePollingEndpointJson(command, command.AgentVersion);

        var machine = BuildMachineDefaults(
            command.MachineName ?? $"tentacle-{command.SubscriptionId[..8]}",
            SerializeRolesFromCsv(command.Roles), resolvedEnvironmentIds, command.SpaceId, endpointJson, command.MachinePolicyId);

        return machine;
    }
}
