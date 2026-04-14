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
        var resolvedEnvironmentIds = await ResolveEnvironmentIdsAsync(command.Environments, cancellationToken).ConfigureAwait(false);

        var existing = await _dataProvider.GetMachineBySubscriptionIdAsync(command.SubscriptionId, cancellationToken).ConfigureAwait(false);

        Machine machine;

        var serializedRoles = SerializeRolesFromCsv(command.Roles);

        if (existing != null)
        {
            var agentVersion = command.AgentVersion ?? EndpointJsonHelper.GetField(existing.Endpoint, "AgentVersion");

            existing.Roles = serializedRoles ?? existing.Roles;
            existing.EnvironmentIds = resolvedEnvironmentIds ?? existing.EnvironmentIds;
            existing.Endpoint = BuildKubernetesAgentEndpointJson(command, agentVersion);

            await _dataProvider.UpdateMachineAsync(existing, cancellationToken: cancellationToken).ConfigureAwait(false);

            machine = existing;

            Log.Information("Updated existing machine {MachineName} ({SubscriptionId})", machine.Name, command.SubscriptionId);
        }
        else
        {
            machine = BuildKubernetesAgentMachine(command, resolvedEnvironmentIds);

            await EnsureUniqueNameAsync(machine.Name, machine.SpaceId, cancellationToken).ConfigureAwait(false);
            await AssignDefaultPolicyAsync(machine, cancellationToken).ConfigureAwait(false);
            await _dataProvider.AddMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

            Log.Information("Registered new machine {MachineName} ({SubscriptionId})", machine.Name, command.SubscriptionId);
        }

        _trustDistributor.Reconfigure();

        return new RegisterMachineResponseData
        {
            MachineId = machine.Id,
            ServerThumbprint = GetServerThumbprint(),
            SubscriptionUri = $"poll://{command.SubscriptionId}/"
        };
    }

    private static string BuildKubernetesAgentEndpointJson(RegisterKubernetesAgentCommand command, string agentVersion)
    {
        return JsonSerializer.Serialize(new KubernetesAgentEndpointDto
        {
            Namespace = command.Namespace,
            Thumbprint = command.Thumbprint,
            SubscriptionId = command.SubscriptionId,
            ReleaseName = command.ReleaseName,
            HelmNamespace = command.HelmNamespace,
            ChartRef = command.ChartRef,
            CommunicationStyle = nameof(CommunicationStyleEnum.KubernetesAgent),
            AgentVersion = agentVersion
        });
    }

    private Machine BuildKubernetesAgentMachine(RegisterKubernetesAgentCommand command, string resolvedEnvironmentIds)
    {
        var endpointJson = BuildKubernetesAgentEndpointJson(command, command.AgentVersion);

        var machine = BuildMachineDefaults(
            command.MachineName ?? $"machine-{command.SubscriptionId[..8]}",
            SerializeRolesFromCsv(command.Roles), resolvedEnvironmentIds, command.SpaceId, endpointJson, command.MachinePolicyId);

        return machine;
    }
}
