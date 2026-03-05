using System.Text.Json;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Deployments.Machine;
using Squid.Core.Persistence.Entities.Deployments;
using CommunicationStyleEnum = Squid.Message.Enums.CommunicationStyle;

namespace Squid.Core.Services.Machines;

public partial class MachineRegistrationService
{
    public async Task<RegisterMachineResponseData> RegisterKubernetesApiAsync(RegisterKubernetesApiCommand command, CancellationToken cancellationToken = default)
    {
        var endpointJson = BuildKubernetesApiEndpointJson(command);
        var machine = BuildKubernetesApiMachine(command, endpointJson);

        await _dataProvider.AddMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new RegisterMachineResponseData
        {
            MachineId = machine.Id,
        };
    }

    private static string BuildKubernetesApiEndpointJson(RegisterKubernetesApiCommand command)
    {
        return JsonSerializer.Serialize(new KubernetesApiEndpointDto
        {
            ClusterUrl = command.ClusterUrl,
            Namespace = command.Namespace,
            SkipTlsVerification = command.SkipTlsVerification.ToString(),
            ResourceReferences = command.ResourceReferences,
            CommunicationStyle = nameof(CommunicationStyleEnum.KubernetesApi)
        });
    }

    private static Machine BuildKubernetesApiMachine(RegisterKubernetesApiCommand command, string endpointJson)
    {
        var serializedRoles = command.Roles != null ? JsonSerializer.Serialize(command.Roles) : null;
        var serializedEnvIds = command.EnvironmentIds != null ? JsonSerializer.Serialize(command.EnvironmentIds) : null;

        var machine = BuildMachineDefaults(
            command.MachineName ?? $"k8s-api-{Guid.NewGuid():N[..8]}",
            serializedRoles, serializedEnvIds, command.SpaceId, endpointJson);

        machine.Uri = command.ClusterUrl ?? string.Empty;

        return machine;
    }
}
