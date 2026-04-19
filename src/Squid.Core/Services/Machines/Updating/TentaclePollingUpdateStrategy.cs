using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.Updating;

/// <summary>
/// TentaclePolling editable fields: SubscriptionId (re-bind) and Thumbprint
/// (certificate rotation). AgentVersion is NOT here — it's a read-only
/// field populated by the health-check Capabilities probe, not an
/// operator-settable value.
/// </summary>
public sealed class TentaclePollingUpdateStrategy : MachineUpdateStrategyBase<TentaclePollingEndpointDto>
{
    protected override string StyleName => nameof(CommunicationStyle.TentaclePolling);

    protected override IReadOnlySet<string> OwnedFieldNames { get; } = new HashSet<string>
    {
        nameof(UpdateMachineCommand.SubscriptionId),
        nameof(UpdateMachineCommand.Thumbprint),
    };

    protected override void ApplyOwnedFields(TentaclePollingEndpointDto e, UpdateMachineCommand c)
    {
        e.SubscriptionId = c.SubscriptionId ?? e.SubscriptionId;
        e.Thumbprint = c.Thumbprint ?? e.Thumbprint;
    }
}
