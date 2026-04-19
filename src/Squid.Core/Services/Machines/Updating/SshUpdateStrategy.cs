using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.Updating;

public sealed class SshUpdateStrategy : MachineUpdateStrategyBase<SshEndpointDto>
{
    protected override string StyleName => nameof(CommunicationStyle.Ssh);

    protected override IReadOnlySet<string> OwnedFieldNames { get; } = new HashSet<string>
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

    protected override void ApplyOwnedFields(SshEndpointDto e, UpdateMachineCommand c)
    {
        e.Host = c.Host ?? e.Host;
        e.Port = c.Port ?? e.Port;
        e.Fingerprint = c.Fingerprint ?? e.Fingerprint;
        e.RemoteWorkingDirectory = c.RemoteWorkingDirectory ?? e.RemoteWorkingDirectory;
        e.ProxyType = c.ProxyType ?? e.ProxyType;
        e.ProxyHost = c.ProxyHost ?? e.ProxyHost;
        e.ProxyPort = c.ProxyPort ?? e.ProxyPort;
        e.ProxyUsername = c.ProxyUsername ?? e.ProxyUsername;
        e.ProxyPassword = c.ProxyPassword ?? e.ProxyPassword;
        e.ResourceReferences = c.ResourceReferences ?? e.ResourceReferences;
    }
}
