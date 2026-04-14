using System.Text.Json;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Deployments.Machine;
using Squid.Core.Persistence.Entities.Deployments;
using CommunicationStyleEnum = Squid.Message.Enums.CommunicationStyle;

namespace Squid.Core.Services.Machines;

public partial class MachineRegistrationService
{
    public async Task<RegisterMachineResponseData> RegisterSshAsync(RegisterSshCommand command, CancellationToken cancellationToken = default)
    {
        var endpointJson = BuildSshEndpointJson(command);
        var machine = BuildSshMachine(command, endpointJson);

        await EnsureUniqueNameAsync(machine.Name, command.SpaceId, cancellationToken).ConfigureAwait(false);
        await AssignDefaultPolicyAsync(machine, cancellationToken).ConfigureAwait(false);
        await _dataProvider.AddMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new RegisterMachineResponseData
        {
            MachineId = machine.Id,
        };
    }

    private static string BuildSshEndpointJson(RegisterSshCommand command)
    {
        return JsonSerializer.Serialize(new SshEndpointDto
        {
            CommunicationStyle = nameof(CommunicationStyleEnum.Ssh),
            Host = command.Host,
            Port = command.Port > 0 ? command.Port : 22,
            Fingerprint = command.Fingerprint,
            RemoteWorkingDirectory = command.RemoteWorkingDirectory,
            ProxyType = command.ProxyType,
            ProxyHost = command.ProxyHost,
            ProxyPort = command.ProxyPort,
            ProxyUsername = command.ProxyUsername,
            ProxyPassword = command.ProxyPassword,
            ResourceReferences = command.ResourceReferences
        });
    }

    private static Machine BuildSshMachine(RegisterSshCommand command, string endpointJson)
    {
        var serializedRoles = command.Roles != null ? JsonSerializer.Serialize(command.Roles) : null;
        var serializedEnvIds = command.EnvironmentIds != null ? JsonSerializer.Serialize(command.EnvironmentIds) : null;

        var machine = BuildMachineDefaults(
            command.MachineName ?? $"ssh-{Guid.NewGuid():N}"[..20],
            serializedRoles, serializedEnvIds, command.SpaceId, endpointJson, command.MachinePolicyId);

        return machine;
    }
}
