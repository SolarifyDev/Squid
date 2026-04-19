using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.Updating;

public sealed class KubernetesApiUpdateStrategy : MachineUpdateStrategyBase<KubernetesApiEndpointDto>
{
    protected override string StyleName => nameof(CommunicationStyle.KubernetesApi);

    protected override IReadOnlySet<string> OwnedFieldNames { get; } = new HashSet<string>
    {
        nameof(UpdateMachineCommand.ClusterUrl),
        nameof(UpdateMachineCommand.Namespace),
        nameof(UpdateMachineCommand.SkipTlsVerification),
        nameof(UpdateMachineCommand.ProviderType),
        nameof(UpdateMachineCommand.ProviderConfig),
        nameof(UpdateMachineCommand.ResourceReferences),
    };

    protected override void ApplyOwnedFields(KubernetesApiEndpointDto e, UpdateMachineCommand c)
    {
        e.ClusterUrl = c.ClusterUrl ?? e.ClusterUrl;
        e.Namespace = c.Namespace ?? e.Namespace;
        e.SkipTlsVerification = c.SkipTlsVerification?.ToString() ?? e.SkipTlsVerification;
        e.ProviderType = c.ProviderType ?? e.ProviderType;
        e.ProviderConfig = c.ProviderConfig ?? e.ProviderConfig;
        e.ResourceReferences = c.ResourceReferences ?? e.ResourceReferences;
    }
}
