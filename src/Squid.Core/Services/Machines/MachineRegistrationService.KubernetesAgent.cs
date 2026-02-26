using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums;
using Squid.Message.Commands.Machine;
using CommunicationStyleEnum = Squid.Message.Enums.CommunicationStyle;

namespace Squid.Core.Services.Machines;

public partial class MachineRegistrationService
{
    public async Task<RegisterMachineResponseData> RegisterKubernetesAgentAsync(RegisterKubernetesAgentCommand command, CancellationToken cancellationToken = default)
    {
        _halibutRuntime.Trust(command.Thumbprint);

        var existing = await _dataProvider.GetMachineBySubscriptionIdAsync(command.SubscriptionId, cancellationToken).ConfigureAwait(false);

        Machine machine;

        if (existing != null)
        {
            existing.Thumbprint = command.Thumbprint;
            existing.Roles = command.Roles ?? existing.Roles;
            existing.EnvironmentIds = command.EnvironmentIds ?? existing.EnvironmentIds;
            existing.Endpoint = BuildKubernetesAgentEndpointJson(command);

            await _dataProvider.UpdateMachineAsync(existing, cancellationToken: cancellationToken).ConfigureAwait(false);

            machine = existing;

            Log.Information("Updated existing machine {MachineName} ({SubscriptionId})", machine.Name, machine.PollingSubscriptionId);
        }
        else
        {
            machine = BuildKubernetesAgentMachine(command);

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
        return JsonSerializer.Serialize(new
        {
            command.Namespace,
            command.Thumbprint,
            command.SubscriptionId,
            CommunicationStyle = nameof(CommunicationStyleEnum.KubernetesAgent),
        });
    }

    private static Machine BuildKubernetesAgentMachine(RegisterKubernetesAgentCommand command)
    {
        var endpointJson = BuildKubernetesAgentEndpointJson(command);

        return new Machine
        {
            Name = command.MachineName ?? $"machine-{command.SubscriptionId[..8]}",
            IsDisabled = false,
            Roles = command.Roles ?? string.Empty,
            EnvironmentIds = command.EnvironmentIds ?? string.Empty,
            Json = string.Empty,
            Thumbprint = command.Thumbprint,
            Uri = string.Empty,
            HasLatestCalamari = false,
            Endpoint = endpointJson,
            DataVersion = Array.Empty<byte>(),
            SpaceId = command.SpaceId,
            OperatingSystem = OperatingSystemType.Linux,
            ShellName = "Bash",
            ShellVersion = string.Empty,
            LicenseHash = string.Empty,
            Slug = $"machine-{Guid.NewGuid():N}",
            PollingSubscriptionId = command.SubscriptionId
        };
    }
}
