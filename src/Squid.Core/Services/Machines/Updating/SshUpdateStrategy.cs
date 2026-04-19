using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Json;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.Updating;

public sealed class SshUpdateStrategy : IMachineUpdateStrategy
{
    private static readonly IReadOnlySet<string> OwnedFields = new HashSet<string>
    {
        nameof(UpdateMachineCommand.Host),
        nameof(UpdateMachineCommand.Port),
        nameof(UpdateMachineCommand.Fingerprint),
        nameof(UpdateMachineCommand.RemoteWorkingDirectory),
        nameof(UpdateMachineCommand.ProxyType),
        nameof(UpdateMachineCommand.ProxyHost),
        nameof(UpdateMachineCommand.ProxyPort),
        nameof(UpdateMachineCommand.ProxyUsername),
        nameof(UpdateMachineCommand.ProxyPassword),
        nameof(UpdateMachineCommand.ResourceReferences),
    };

    public bool CanHandle(string communicationStyle)
        => communicationStyle == nameof(CommunicationStyle.Ssh);

    public void ValidateForStyle(int machineId, UpdateMachineCommand command)
        => CrossStyleContaminationGuard.ThrowIfCommandTouchesNonOwnedFields(
            machineId, nameof(CommunicationStyle.Ssh), OwnedFields, command);

    public bool ApplyEndpointUpdate(Machine machine, UpdateMachineCommand c)
    {
        if (c.Host == null && !c.Port.HasValue && c.Fingerprint == null && c.RemoteWorkingDirectory == null
            && !c.ProxyType.HasValue && c.ProxyHost == null && !c.ProxyPort.HasValue
            && c.ProxyUsername == null && c.ProxyPassword == null && c.ResourceReferences == null)
            return false;

        var endpoint = !string.IsNullOrEmpty(machine.Endpoint)
            ? JsonSerializer.Deserialize<SshEndpointDto>(machine.Endpoint, SquidJsonDefaults.CaseInsensitive)
            : new SshEndpointDto();

        if (c.Host != null) endpoint.Host = c.Host;
        if (c.Port.HasValue) endpoint.Port = c.Port.Value;
        if (c.Fingerprint != null) endpoint.Fingerprint = c.Fingerprint;
        if (c.RemoteWorkingDirectory != null) endpoint.RemoteWorkingDirectory = c.RemoteWorkingDirectory;
        if (c.ProxyType.HasValue) endpoint.ProxyType = c.ProxyType.Value;
        if (c.ProxyHost != null) endpoint.ProxyHost = c.ProxyHost;
        if (c.ProxyPort.HasValue) endpoint.ProxyPort = c.ProxyPort.Value;
        if (c.ProxyUsername != null) endpoint.ProxyUsername = c.ProxyUsername;
        if (c.ProxyPassword != null) endpoint.ProxyPassword = c.ProxyPassword;
        if (c.ResourceReferences != null) endpoint.ResourceReferences = c.ResourceReferences;

        machine.Endpoint = JsonSerializer.Serialize(endpoint);
        return true;
    }
}
