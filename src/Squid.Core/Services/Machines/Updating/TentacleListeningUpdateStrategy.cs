using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.Updating;

/// <summary>
/// TentacleListening editable fields: Uri (machine IP change), Thumbprint
/// (cert rotation), ProxyId (proxy swap).
/// </summary>
public sealed class TentacleListeningUpdateStrategy : MachineUpdateStrategyBase<TentacleListeningEndpointDto>
{
    protected override string StyleName => nameof(CommunicationStyle.TentacleListening);

    protected override IReadOnlySet<string> OwnedFieldNames { get; } = new HashSet<string>
    {
        nameof(UpdateMachineCommand.Uri),
        nameof(UpdateMachineCommand.Thumbprint),
        nameof(UpdateMachineCommand.ProxyId),
    };

    protected override void ApplyOwnedFields(TentacleListeningEndpointDto e, UpdateMachineCommand c)
    {
        e.Uri = c.Uri ?? e.Uri;
        e.Thumbprint = c.Thumbprint ?? e.Thumbprint;
        e.ProxyId = c.ProxyId ?? e.ProxyId;
    }
}
