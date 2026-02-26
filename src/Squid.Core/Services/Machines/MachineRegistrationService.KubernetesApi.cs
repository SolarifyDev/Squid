using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Deployments.Machine;
using CommunicationStyleEnum = Squid.Message.Enums.CommunicationStyle;

namespace Squid.Core.Services.Machines;

public partial class MachineRegistrationService
{
    public async Task<RegisterMachineResponseData> RegisterKubernetesApiAsync(
        RegisterKubernetesApiCommand command,
        CancellationToken cancellationToken = default)
    {
        var endpointJson = BuildKubernetesApiEndpointJson(command);
        var machine = BuildKubernetesApiMachine(command, endpointJson);

        await _dataProvider.AddMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

        Log.Information("Registered KubernetesApi machine {MachineName} with account {DeploymentAccountId}", machine.Name, command.DeploymentAccountId);

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
            ClusterCertificate = command.ClusterCertificate,
            DeploymentAccountId = command.DeploymentAccountId.ToString(),
            CommunicationStyle = nameof(CommunicationStyleEnum.KubernetesApi)
        });
    }

    private static Machine BuildKubernetesApiMachine(RegisterKubernetesApiCommand command, string endpointJson)
    {
        var machine = BuildMachineDefaults(
            command.MachineName ?? $"k8s-api-{Guid.NewGuid():N[..8]}",
            command.Roles, command.EnvironmentIds, command.SpaceId, endpointJson);

        machine.Uri = command.ClusterUrl ?? string.Empty;

        return machine;
    }
}
