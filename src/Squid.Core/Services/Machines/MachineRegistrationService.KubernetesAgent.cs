using System.Text.Json;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Deployments.Machine;
using Squid.Core.Persistence.Entities.Deployments;
using CommunicationStyleEnum = Squid.Message.Enums.CommunicationStyle;

namespace Squid.Core.Services.Machines;

public partial class MachineRegistrationService
{
    public async Task<RegisterMachineResponseData> RegisterKubernetesAgentAsync(RegisterKubernetesAgentCommand command, CancellationToken cancellationToken = default)
    {
        _halibutRuntime.Trust(command.Thumbprint);

        var resolvedEnvironmentIds = await ResolveEnvironmentIdsAsync(command.Environments, cancellationToken).ConfigureAwait(false);

        var existing = await _dataProvider.GetMachineBySubscriptionIdAsync(command.SubscriptionId, cancellationToken).ConfigureAwait(false);

        Machine machine;

        var serializedRoles = SerializeRolesFromCsv(command.Roles);

        if (existing != null)
        {
            existing.Thumbprint = command.Thumbprint;
            existing.Roles = serializedRoles ?? existing.Roles;
            existing.EnvironmentIds = resolvedEnvironmentIds ?? existing.EnvironmentIds;
            existing.Endpoint = BuildKubernetesAgentEndpointJson(command);

            await _dataProvider.UpdateMachineAsync(existing, cancellationToken: cancellationToken).ConfigureAwait(false);

            machine = existing;

            Log.Information("Updated existing machine {MachineName} ({SubscriptionId})", machine.Name, machine.PollingSubscriptionId);
        }
        else
        {
            machine = BuildKubernetesAgentMachine(command, resolvedEnvironmentIds);

            await _dataProvider.AddMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

            Log.Information("Registered new machine {MachineName} ({SubscriptionId})", machine.Name, machine.PollingSubscriptionId);
        }

        return new RegisterMachineResponseData
        {
            MachineId = machine.Id,
            ServerThumbprint = GetServerThumbprint(),
            SubscriptionUri = $"poll://{command.SubscriptionId}/"
        };
    }

    private static string BuildKubernetesAgentEndpointJson(RegisterKubernetesAgentCommand command)
    {
        return JsonSerializer.Serialize(new KubernetesAgentEndpointDto
        {
            Namespace = command.Namespace,
            Thumbprint = command.Thumbprint,
            SubscriptionId = command.SubscriptionId,
            CommunicationStyle = nameof(CommunicationStyleEnum.KubernetesAgent)
        });
    }

    private Machine BuildKubernetesAgentMachine(RegisterKubernetesAgentCommand command, string resolvedEnvironmentIds)
    {
        var endpointJson = BuildKubernetesAgentEndpointJson(command);

        var machine = BuildMachineDefaults(
            command.MachineName ?? $"machine-{command.SubscriptionId[..8]}",
            SerializeRolesFromCsv(command.Roles), resolvedEnvironmentIds, command.SpaceId, endpointJson);

        machine.Thumbprint = command.Thumbprint;
        machine.PollingSubscriptionId = command.SubscriptionId;

        return machine;
    }
}
