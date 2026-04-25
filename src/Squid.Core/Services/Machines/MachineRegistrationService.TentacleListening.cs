using System.Text.Json;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Deployments.Machine;
using Squid.Core.Persistence.Entities.Deployments;
using CommunicationStyleEnum = Squid.Message.Enums.CommunicationStyle;

namespace Squid.Core.Services.Machines;

public partial class MachineRegistrationService
{
    public async Task<RegisterMachineResponseData> RegisterTentacleListeningAsync(RegisterTentacleListeningCommand command, CancellationToken cancellationToken = default)
    {
        var resolvedEnvironmentIds = await ResolveEnvironmentIdsAsync(command.Environments, cancellationToken).ConfigureAwait(false);

        var existing = await _dataProvider.GetMachineByEndpointUriAsync(command.Uri, cancellationToken).ConfigureAwait(false);

        Machine machine;

        var serializedRoles = SerializeRolesFromCsv(command.Roles);

        if (existing != null)
        {
            existing.Endpoint = BuildTentacleListeningEndpointJson(command);

            if (serializedRoles != null) existing.Roles = serializedRoles;
            if (resolvedEnvironmentIds != null) existing.EnvironmentIds = resolvedEnvironmentIds;

            await _dataProvider.UpdateMachineAsync(existing, cancellationToken: cancellationToken).ConfigureAwait(false);

            machine = existing;

            Log.Information("Updated existing TentacleListening machine {MachineName} (Uri={Uri})", machine.Name, command.Uri);
        }
        else
        {
            machine = BuildTentacleListeningMachine(command, BuildTentacleListeningEndpointJson(command), serializedRoles, resolvedEnvironmentIds);

            await EnsureUniqueNameAsync(machine.Name, command.SpaceId, cancellationToken).ConfigureAwait(false);
            await AssignDefaultPolicyAsync(machine, cancellationToken).ConfigureAwait(false);
            await _dataProvider.AddMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

            Log.Information("Registered new TentacleListening machine {MachineName} (Uri={Uri})", machine.Name, command.Uri);
        }

        // Server reaches out to listening Tentacles, but mutual TLS still requires the
        // server's Halibut runtime to trust the agent's thumbprint before it will accept
        // the return traffic. Polling registration already calls this; before this change
        // the listening path silently omitted it, so a newly-registered listening agent
        // could not complete a deployment until the next server restart.
        // P1 (Phase-6): switched to async to keep the Kestrel request thread
        // off the DB-bound sync-over-async path.
        await _trustDistributor.ReconfigureIfMissingAsync(command.Thumbprint, cancellationToken).ConfigureAwait(false);

        return new RegisterMachineResponseData
        {
            MachineId = machine.Id,
            ServerThumbprint = GetServerThumbprint()
        };
    }

    private static string BuildTentacleListeningEndpointJson(RegisterTentacleListeningCommand command)
    {
        return JsonSerializer.Serialize(new TentacleListeningEndpointDto
        {
            CommunicationStyle = nameof(CommunicationStyleEnum.TentacleListening),
            Uri = command.Uri,
            Thumbprint = command.Thumbprint,
            AgentVersion = command.AgentVersion
        });
    }

    private static Machine BuildTentacleListeningMachine(RegisterTentacleListeningCommand command, string endpointJson, string serializedRoles, string resolvedEnvironmentIds)
    {
        return BuildMachineDefaults(
            command.MachineName ?? $"tentacle-{Guid.NewGuid():N}"[..20],
            serializedRoles, resolvedEnvironmentIds, command.SpaceId, endpointJson, command.MachinePolicyId);
    }
}
