using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Phase 2 placeholder. Kubernetes Agent upgrades will go through
/// <c>helm upgrade --install --reuse-values --set image.tag=&lt;version&gt;</c>
/// against the cluster's k8s API; the agent doesn't participate (no Halibut
/// RPC needed — k8s does the rolling update + automatic rollback). Until
/// that's implemented, return <c>NotSupported</c> so the operator gets a
/// clear "use helm directly" signal instead of a silent failure.
/// </summary>
public sealed class KubernetesAgentUpgradeStrategy : IMachineUpgradeStrategy
{
    public bool CanHandle(string communicationStyle)
        => communicationStyle == nameof(CommunicationStyle.KubernetesAgent);

    public Task<MachineUpgradeOutcome> UpgradeAsync(Machine machine, string targetVersion, CancellationToken ct)
    {
        return Task.FromResult(new MachineUpgradeOutcome
        {
            Status = MachineUpgradeStatus.NotSupported,
            Detail =
                "Kubernetes Agent upgrade via the UI is planned for Phase 2 of the self-upgrade roadmap. " +
                "For now, run helm upgrade against the cluster directly: " +
                $"helm upgrade --reuse-values --set tentacle.image.tag={targetVersion} <release-name> <chart>"
        });
    }
}
