using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.Updating;

public sealed class KubernetesAgentUpdateStrategy : MachineUpdateStrategyBase<KubernetesAgentEndpointDto>
{
    protected override string StyleName => nameof(CommunicationStyle.KubernetesAgent);

    protected override IReadOnlySet<string> OwnedFieldNames { get; } = new HashSet<string>
    {
        nameof(UpdateMachineCommand.SubscriptionId),
        nameof(UpdateMachineCommand.Thumbprint),
        nameof(UpdateMachineCommand.Namespace),
        nameof(UpdateMachineCommand.ReleaseName),
        nameof(UpdateMachineCommand.HelmNamespace),
        nameof(UpdateMachineCommand.ChartRef),
    };

    protected override void ApplyOwnedFields(KubernetesAgentEndpointDto e, UpdateMachineCommand c)
    {
        e.SubscriptionId = c.SubscriptionId ?? e.SubscriptionId;
        e.Thumbprint = c.Thumbprint ?? e.Thumbprint;
        e.Namespace = c.Namespace ?? e.Namespace;
        e.ReleaseName = c.ReleaseName ?? e.ReleaseName;
        e.HelmNamespace = c.HelmNamespace ?? e.HelmNamespace;
        e.ChartRef = c.ChartRef ?? e.ChartRef;
    }
}
