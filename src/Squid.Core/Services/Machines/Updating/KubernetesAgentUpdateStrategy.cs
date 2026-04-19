using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Json;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.Updating;

/// <summary>
/// KubernetesAgent endpoint fields. Most are effectively "where the agent
/// lives on the cluster" — editing them doesn't migrate the helm release;
/// operator is expected to know that changes here should mirror what they
/// plan to <c>helm upgrade</c> to. Namespace is shared with KubernetesApi
/// field-wise (same name) — contamination guard disambiguates via style.
/// </summary>
public sealed class KubernetesAgentUpdateStrategy : IMachineUpdateStrategy
{
    private static readonly IReadOnlySet<string> OwnedFields = new HashSet<string>
    {
        nameof(UpdateMachineCommand.SubscriptionId),
        nameof(UpdateMachineCommand.Thumbprint),
        nameof(UpdateMachineCommand.Namespace),
        nameof(UpdateMachineCommand.ReleaseName),
        nameof(UpdateMachineCommand.HelmNamespace),
        nameof(UpdateMachineCommand.ChartRef),
    };

    public bool CanHandle(string communicationStyle)
        => communicationStyle == nameof(CommunicationStyle.KubernetesAgent);

    public void ValidateForStyle(int machineId, UpdateMachineCommand command)
        => CrossStyleContaminationGuard.ThrowIfCommandTouchesNonOwnedFields(
            machineId, nameof(CommunicationStyle.KubernetesAgent), OwnedFields, command);

    public bool ApplyEndpointUpdate(Machine machine, UpdateMachineCommand c)
    {
        if (c.SubscriptionId == null && c.Thumbprint == null && c.Namespace == null
            && c.ReleaseName == null && c.HelmNamespace == null && c.ChartRef == null)
            return false;

        var endpoint = !string.IsNullOrEmpty(machine.Endpoint)
            ? JsonSerializer.Deserialize<KubernetesAgentEndpointDto>(machine.Endpoint, SquidJsonDefaults.CaseInsensitive)
            : new KubernetesAgentEndpointDto();

        if (c.SubscriptionId != null) endpoint.SubscriptionId = c.SubscriptionId;
        if (c.Thumbprint != null) endpoint.Thumbprint = c.Thumbprint;
        if (c.Namespace != null) endpoint.Namespace = c.Namespace;
        if (c.ReleaseName != null) endpoint.ReleaseName = c.ReleaseName;
        if (c.HelmNamespace != null) endpoint.HelmNamespace = c.HelmNamespace;
        if (c.ChartRef != null) endpoint.ChartRef = c.ChartRef;

        machine.Endpoint = JsonSerializer.Serialize(endpoint);
        return true;
    }
}
